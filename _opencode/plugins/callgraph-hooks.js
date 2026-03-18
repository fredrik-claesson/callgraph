export const CallGraphHooksPlugin = async () => {
  const isBashLike = (tool) => tool === "bash" || tool === "powershell"

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

  return {
    "tool.execute.before": async (input, output) => {
      if (!isBashLike(input.tool)) return

      const args = output.args || {}
      const command = typeof args.command === "string" ? args.command : ""
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

      if (/^\s*callgraph\b/i.test(command)) return

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
