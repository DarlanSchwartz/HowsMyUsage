$ErrorActionPreference = 'Stop'
$exe = Join-Path $PSScriptRoot 'Usage.exe'
$log = Join-Path $PSScriptRoot 'startup.log'
try {
    # The desktop and secondary drives may not be ready immediately after sign-in.
    Start-Sleep -Seconds 15
    for ($attempt = 1; $attempt -le 6; $attempt++) {
        if ((Test-Path -LiteralPath $exe) -and (Get-Process explorer -ErrorAction SilentlyContinue)) {
            $running = Get-Process Usage -ErrorAction SilentlyContinue | Where-Object Path -EQ $exe
            if ($running) { return }
            $process = Start-Process -FilePath $exe -WorkingDirectory $PSScriptRoot -WindowStyle Hidden -PassThru
            Start-Sleep -Seconds 10
            $process.Refresh()
            if (!$process.HasExited) {
                Set-Content -LiteralPath $log -Value "$(Get-Date -Format o) Started successfully (PID $($process.Id))."
                return
            }
            Set-Content -LiteralPath $log -Value "$(Get-Date -Format o) Exit code $($process.ExitCode), attempt $attempt."
        }
        Start-Sleep -Seconds 10
    }
} catch {
    Set-Content -LiteralPath $log -Value "$(Get-Date -Format o) $($_.Exception.Message)"
}
