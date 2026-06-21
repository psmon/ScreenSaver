# claude-status-hook.ps1
# Forwards a Claude Code hook event to the Screensaver Overlay monitor over loopback UDP.
# Fire-and-forget: if the overlay isn't listening the datagram is just dropped. Configured in
# ~/.claude/settings.json with "async": true so it never blocks Claude Code. Always exits 0.

$ErrorActionPreference = 'SilentlyContinue'
try {
    $raw = [System.IO.StreamReader]::new([System.Console]::OpenStandardInput()).ReadToEnd()
    if (-not $raw) { exit 0 }
    $e = $raw | ConvertFrom-Json

    function Clean([string]$s, [int]$max = 400) {
        if (-not $s) { return '' }
        $s = ($s -replace '\s+', ' ').Trim()
        if ($s.Length -gt $max) { $s = $s.Substring(0, $max) + '...' }
        return $s
    }

    # Build a rich one-line description of a tool call from its inputs.
    function Tool-Detail($e) {
        $t = $e.tool_name
        $i = $e.tool_input
        $d = ''
        switch -Regex ($t) {
            '^(Bash|PowerShell)$' { $d = $i.command; if ($i.description) { $d = "$($i.description) :: $d" } }
            '^Edit$'              { $d = $i.file_path }
            '^MultiEdit$'         { $d = $i.file_path }
            '^Write$'             { $d = $i.file_path }
            '^Read$'              { $d = $i.file_path }
            '^NotebookEdit$'      { $d = $i.notebook_path }
            '^Grep$'              { $d = "/$($i.pattern)/ $($i.path)" }
            '^Glob$'              { $d = $i.pattern }
            '^Task$'              { $d = "$($i.subagent_type): $($i.description)" }
            '^WebFetch$'          { $d = $i.url }
            '^WebSearch$'         { $d = $i.query }
            '^Skill$'             { $d = $i.skill }
            default               { $d = '' }
        }
        return (("{0}  {1}" -f $t, (Clean $d 320)).Trim())
    }

    # Pull the most recent assistant text message out of the JSONL transcript so the console
    # can show what Claude actually said this turn, not just which tools ran.
    function Last-AssistantText([string]$path) {
        if (-not $path -or -not (Test-Path $path)) { return '' }
        # transcript is UTF-8 JSONL; read it as UTF-8 (Windows PowerShell defaults to ANSI)
        $lines = Get-Content $path -Tail 80 -Encoding UTF8 -ErrorAction SilentlyContinue
        for ($k = $lines.Count - 1; $k -ge 0; $k--) {
            $o = $null
            try { $o = $lines[$k] | ConvertFrom-Json } catch { continue }
            $m = $o.message
            if ($m -and $m.role -eq 'assistant' -and $m.content) {
                $texts = @()
                foreach ($c in $m.content) { if ($c.type -eq 'text' -and $c.text) { $texts += $c.text } }
                if ($texts.Count) { return ($texts -join ' ') }
            }
        }
        return ''
    }

    $kind = 'info'; $text = ''
    switch ($e.hook_event_name) {
        'UserPromptSubmit' { $kind = 'prompt'; $text = '> ' + (Clean $e.prompt 500) }
        'PreToolUse'       { $kind = 'tool';   $text = '-> ' + (Tool-Detail $e) }
        'PostToolUse'      { $kind = 'done';   $text = 'OK ' + $e.tool_name }
        'Notification'     { $kind = 'notify'; $text = '! ' + (Clean $e.message) }
        'Stop'             {
            $say = Clean (Last-AssistantText $e.transcript_path) 600
            if ($say) { $kind = 'say'; $text = '>> ' + $say }
            else { $kind = 'idle'; $text = 'idle - awaiting input' }
        }
        'SessionStart'     { $kind = 'sys'; $text = 'session start: ' + (Split-Path $e.cwd -Leaf) }
        'SessionEnd'       { $kind = 'sys'; $text = 'session end' }
        default            { $kind = 'info'; $text = [string]$e.hook_event_name }
    }
    if (-not $text) { exit 0 }

    $payload = "$kind|$text"
    $bytes = [System.Text.Encoding]::UTF8.GetBytes($payload)
    $udp = New-Object System.Net.Sockets.UdpClient
    [void]$udp.Send($bytes, $bytes.Length, '127.0.0.1', 47921)
    $udp.Close()
} catch { }
exit 0
