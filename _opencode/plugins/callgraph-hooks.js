import { createHash } from "node:crypto"
import { promises as fs } from "node:fs"
import os from "node:os"
import path from "node:path"

export const CallGraphHooksPlugin = async () => {
  const fallbackAfterFailuresRaw = process.env.OPENCODE_CALLGRAPH_FALLBACK_AFTER_FAILURES
  const fallbackAfterFailures = Number.isFinite(Number(fallbackAfterFailuresRaw))
    ? Math.max(0, Number.parseInt(fallbackAfterFailuresRaw ?? "2", 10))
    : 2
  const policyModeRaw = (process.env.OPENCODE_CALLGRAPH_POLICY_MODE ?? "warn").toLowerCase()
  const policyMode = policyModeRaw === "deny" ? "deny" : "warn"
  const stateDir = path.join(os.homedir(), ".config", "opencode", "plugins", ".state")

  const isBashLike = (tool) => tool === "bash" || tool === "powershell"

  const firstString = (...values) => {
    for (const value of values) {
      if (typeof value !== "string") continue
      const trimmed = value.trim()
      if (trimmed.length > 0) return trimmed
    }

    return ""
  }

  const extractArg = (command, flag) => {
    const re = new RegExp(`${flag}(?:\\s+|=)(\"[^\"]+\"|'[^']+'|\\S+)`, "i")
    const match = command.match(re)
    if (!match || !match[1]) return ""
    return match[1].replace(/^['\"]|['\"]$/g, "")
  }

  const deny = (message) => {
    throw new Error(message)
  }

  const warn = (message) => {
    console.warn(`[CallGraph hook hint] ${message}`)
  }

  const readCounter = async (filePath) => {
    try {
      const raw = (await fs.readFile(filePath, "utf8")).trim()
      const parsed = Number.parseInt(raw, 10)
      if (Number.isFinite(parsed) && parsed >= 0) return parsed
    } catch {
      // File may not exist yet.
    }

    return 0
  }

  const writeCounter = async (filePath, value) => {
    await fs.mkdir(path.dirname(filePath), { recursive: true })
    await fs.writeFile(filePath, String(value), "utf8")
  }

  const extractCommand = (input, output) =>
    firstString(
      output?.args?.command,
      output?.args?.cmd,
      input?.args?.command,
      input?.args?.cmd,
      output?.command,
      input?.command,
    )

  const extractSessionId = (input, output) =>
    firstString(
      input?.sessionId,
      input?.session_id,
      input?.sessionID,
      input?.conversationId,
      input?.conversation_id,
      input?.threadId,
      input?.thread_id,
      output?.sessionId,
      output?.session_id,
      output?.threadId,
      output?.thread_id,
    )

  const extractCwd = (input, output) =>
    firstString(
      output?.args?.cwd,
      output?.args?.workdir,
      output?.args?.workingDirectory,
      output?.args?.working_directory,
      input?.args?.cwd,
      input?.args?.workdir,
      input?.args?.workingDirectory,
      input?.args?.working_directory,
      input?.cwd,
      input?.workdir,
      input?.workingDirectory,
      input?.working_directory,
      output?.cwd,
      output?.workdir,
      output?.workingDirectory,
      output?.working_directory,
    )

  const sessionKey = (sessionId, cwd) => {
    if (sessionId) {
      return sessionId.replace(/[^A-Za-z0-9._-]/g, "_")
    }

    if (cwd) {
      return createHash("sha256").update(cwd).digest("hex").slice(0, 20)
    }

    return "global"
  }

  const markMainCallgraphUsage = async (input, output) => {
    try {
      const key = sessionKey(extractSessionId(input, output), extractCwd(input, output))
      const filePath = path.join(stateDir, `callgraph-main-count-${key}.txt`)
      const current = await readCounter(filePath)
      await writeCounter(filePath, current + 1)
    } catch {
      // State tracking should never block normal command flow.
    }
  }

  const failureCounterPath = (input, output) => {
    const key = sessionKey(extractSessionId(input, output), extractCwd(input, output))
    return path.join(stateDir, `callgraph-failure-count-${key}.txt`)
  }

  const getFailureCount = async (input, output) => {
    try {
      return await readCounter(failureCounterPath(input, output))
    } catch {
      return 0
    }
  }

  const recordFailure = async (input, output) => {
    try {
      const filePath = failureCounterPath(input, output)
      const current = await readCounter(filePath)
      await writeCounter(filePath, current + 1)
    } catch {
      // Failure tracking should never block normal command flow.
    }
  }

  const resetFailures = async (input, output) => {
    try {
      await writeCounter(failureCounterPath(input, output), 0)
    } catch {
      // Failure tracking should never block normal command flow.
    }
  }

  const denyWithFailure = async (input, output, message) => {
    await recordFailure(input, output)
    if (policyMode === "warn") {
      warn(message)
      return
    }

    deny(message)
  }

  const isNarrowShellFallback = (command) =>
    /^[\s]*(rg|grep|find)\b/i.test(command) &&
    /(\|[\s]*(head|tail)\b|--max-count\b|(^|[\s])-m[\s]+\d+|sed[\s]+-n)/i.test(command)

  return {
    "tool.execute.before": async (input, output) => {
      if (!isBashLike(input.tool)) return

      let command = extractCommand(input, output)
      if (!command.trim()) return

      const isSearchCommand = /\b(find|grep|rg|ls)\b/i.test(command)
      const targetsTests = /((^|[\\/_.-])tests?([\\/_.-]|$)|\.tests?\.csproj\b|[._-]tests?\b|\b(xunit|nunit|mstest)\b)/i.test(command)
      if (isSearchCommand && targetsTests) return

      const originalCommand = command
      if (/^\s*callgraph\s+analyze-callgraph\b/i.test(command)) {
        command = command.replace(/^\s*callgraph\s+analyze-callgraph\b/i, "callgraph analyze")
      }

      if (/^\s*callgraph\s+analyze\b/i.test(command)) {
        command = command.replace(/--filePath\b/gi, "--filepath")
      }

      if (/^\s*callgraph\s+get-method-source\b/i.test(command) && !/--methodName(?:\s+|=)/i.test(command)) {
        command = command.replace(/--method\b/gi, "--methodName")
      }

      if (command !== originalCommand) {
        if (output?.args && typeof output.args === "object") {
          output.args.command = command
        }
      }

      if (/\bcallgraph\b/i.test(command) && /\banalyze\b/i.test(command)) {
        if (!/--file(path|Path)(?:\s+|=)/i.test(command)) {
          await denyWithFailure(input, output, "callgraph analyze requires --filepath <absolute-file.cs>. Example: callgraph analyze --filepath /abs/path/Foo.cs --method Bar --direction outbound --visibility external --depth 2 2>&1")
        }

        const visibility = extractArg(command, "--visibility")
        const depthRaw = extractArg(command, "--depth")
        const depth = Number.isFinite(Number(depthRaw)) ? Number(depthRaw) : 1
        if (visibility.toLowerCase() === "internal" && depth > 2) {
          await denyWithFailure(input, output, "callgraph analyze with --visibility internal supports max --depth 2. Use two-stage analysis: inbound+external depth 2 first, then outbound+internal depth 2 on 1-3 selected methods.")
        }
      }

      if (/\bcallgraph\s+get-method-source\b/i.test(command)) {
        const callCount = (command.match(/callgraph\s+get-method-source/gi) || []).length
        if (callCount > 1 || /&&|;/.test(command)) {
          await denyWithFailure(input, output, "Chained callgraph get-method-source commands are not allowed. Run one get-method-source command per tool call, then summarize.")
        }
      }

      if (/^\s*callgraph\b/i.test(command)) {
        await markMainCallgraphUsage(input, output)
        await resetFailures(input, output)
        return
      }

      const csharpExploration = isSearchCommand && /(\.cs(\b|[^A-Za-z0-9_])|-name\s+[\"']?\*?\.cs|\/src|xargs\s+grep)/i.test(command)
      if (csharpExploration) {
        const failures = await getFailureCount(input, output)
        if (fallbackAfterFailures > 0 && failures >= fallbackAfterFailures && isNarrowShellFallback(command)) {
          return
        }

        await denyWithFailure(input, output, "CallGraph-first policy: do not use rg/find/grep for C# discovery before trying CallGraph. Run callgraph search-file/search-method/list-methods/get-method-source first (daemon, then --no-daemon on failure). Shell fallback is allowed only for explicit test-targeted queries or after repeated CallGraph failures.")
      }
    },
  }
}
