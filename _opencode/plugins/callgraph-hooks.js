import { createHash } from "node:crypto"
import { promises as fs } from "node:fs"
import os from "node:os"
import path from "node:path"

export const CallGraphHooksPlugin = async () => {
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

  const isAbsolutePath = (value) => {
    if (!value) return false
    return /^\//.test(value) || /^[A-Za-z]:[\\/]/.test(value) || /^\\\\/.test(value)
  }

  const deny = (message) => {
    throw new Error(message)
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
      const stateDir = path.join(os.homedir(), ".config", "opencode", "plugins", ".state")
      const filePath = path.join(stateDir, `callgraph-main-count-${key}.txt`)
      await fs.mkdir(stateDir, { recursive: true })

      let current = 0
      try {
        const raw = (await fs.readFile(filePath, "utf8")).trim()
        const parsed = Number.parseInt(raw, 10)
        if (Number.isFinite(parsed) && parsed >= 0) {
          current = parsed
        }
      } catch {
        // File may not exist yet.
      }

      await fs.writeFile(filePath, String(current + 1), "utf8")
    } catch {
      // State tracking should never block normal command flow.
    }
  }

  return {
    "tool.execute.before": async (input, output) => {
      if (!isBashLike(input.tool)) return

      const command = extractCommand(input, output)
      if (!command.trim()) return

      const lower = command.toLowerCase()

      const isSearchCommand = /\b(find|grep|rg|ls)\b/i.test(command)
      const targetsTests = /((^|[\\/_.-])tests?([\\/_.-]|$)|\.tests?\.csproj\b|[._-]tests?\b|\b(xunit|nunit|mstest)\b)/i.test(command)
      if (isSearchCommand && targetsTests) return

      if (/\bcallgraph\b/i.test(command) && /\banalyze\b/i.test(command)) {
        if (/\banalyze-callgraph\b/i.test(command)) {
          deny("Unknown command analyze-callgraph. Use: callgraph analyze --filepath <absolute-file.cs> [--method <name>] [--direction inbound|outbound|bi-directional] [--visibility external|internal] [--depth <n>] 2>&1")
        }

        if (!/--filepath(?:\s+|=)/i.test(command)) {
          deny("callgraph analyze requires --filepath <absolute-file.cs>. Example: callgraph analyze --filepath /abs/path/Foo.cs --method Bar --direction outbound --visibility external --depth 2 2>&1")
        }

        const visibility = extractArg(command, "--visibility")
        const depthRaw = extractArg(command, "--depth")
        const depth = Number.isFinite(Number(depthRaw)) ? Number(depthRaw) : 1
        if (visibility.toLowerCase() === "internal" && depth > 2) {
          deny("callgraph analyze with --visibility internal supports max --depth 2. Use two-stage analysis: inbound+external depth 2 first, then outbound+internal depth 2 on 1-3 selected methods.")
        }
      }

      if (/\bcallgraph\s+get-method-source\b/i.test(command)) {
        const callCount = (command.match(/callgraph\s+get-method-source/gi) || []).length
        if (callCount > 1 || /&&|;/.test(command)) {
          deny("Chained callgraph get-method-source commands are not allowed. Run one get-method-source command per tool call, then summarize.")
        }
      }

      if (/^\s*callgraph\s+(list-methods|get-method-source|search-file|search-method)\b/i.test(command) && /--filePath(?:\s+|=)/i.test(command)) {
        const filePath = extractArg(command, "--filePath")
        if (filePath && !isAbsolutePath(filePath)) {
          deny("callgraph --filePath must be absolute. Use an absolute .cs path, or use --folderPath for scoped discovery first.")
        }
      }

      if (/^\s*callgraph\b/i.test(command)) {
        await markMainCallgraphUsage(input, output)
        return
      }

      const csharpExploration = isSearchCommand && /(\.cs(\b|[^A-Za-z0-9_])|-name\s+[\"']?\*?\.cs|\/src|xargs\s+grep)/i.test(command)
      if (csharpExploration) {
        deny("C# exploration should use CallGraph first. Try callgraph search-file, callgraph list-methods, or callgraph get-method-source.")
      }

      if (lower.includes("callgraph") && lower.includes("--filepath")) {
        output.args.command = command
      }
    },
  }
}
