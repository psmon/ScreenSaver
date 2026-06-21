# claude-status-hook.ps1
# Forwards a Claude Code hook event to the Screensaver Overlay monitor over loopback UDP.
# Fire-and-forget: if the overlay isn't listening the datagram is just dropped. Configured in
# ~/.claude/settings.json with "async": true so it never blocks Claude Code. Always exits 0.

$ErrorActionPreference = 'SilentlyContinue'
try {
    $raw = [System.IO.StreamReader]::new([System.Console]::OpenStandardInput()).ReadToEnd()
    if (-not $raw) { exit 0 }
    $e = $raw | ConvertFrom-Json

    function Clean([string]$s) {
        if (-not $s) { return '' }
        $s = ($s -replace '\s+', ' ').Trim()
        if ($s.Length -gt 160) { $s = $s.Substring(0, 160) + '...' }
        return $s
    }

    function Tool-Detail($e) {
        $t = $e.tool_name
        $i = $e.tool_input
        $d = ''
        switch -Regex ($t) {
            '^(Bash|PowerShell)$'        { $d = $i.command }
            '^(Edit|Write|Read|NotebookEdit)$' { $d = $i.file_path }
            '^Grep$'                     { $d = $i.pattern }
            '^Glob$'                     { $d = $i.pattern }
            '^Task$'                     { $d = $i.description }
            '^WebFetch$'                 { $d = $i.url }
            default                      { $d = '' }
        }
        return (("{0} {1}" -f $t, (Clean $d)).Trim())
    }

    $kind = 'info'; $text = ''
    switch ($e.hook_event_name) {
        'UserPromptSubmit' { $kind = 'prompt'; $text = '> ' + (Clean $e.prompt) }
        'PreToolUse'       { $kind = 'tool';   $text = '-> ' + (Tool-Detail $e) }
        'PostToolUse'      { $kind = 'done';   $text = 'OK ' + $e.tool_name }
        'Notification'     { $kind = 'notify'; $text = '! ' + (Clean $e.message) }
        'Stop'             { $kind = 'idle';   $text = 'idle - awaiting input' }
        'SessionStart'     { $kind = 'sys';    $text = 'session start: ' + (Split-Path $e.cwd -Leaf) }
        'SessionEnd'       { $kind = 'sys';    $text = 'session end' }
        default            { $kind = 'info';   $text = [string]$e.hook_event_name }
    }
    if (-not $text) { exit 0 }

    $payload = "$kind|$text"
    $bytes = [System.Text.Encoding]::UTF8.GetBytes($payload)
    $udp = New-Object System.Net.Sockets.UdpClient
    [void]$udp.Send($bytes, $bytes.Length, '127.0.0.1', 47921)
    $udp.Close()
} catch { }
exit 0
