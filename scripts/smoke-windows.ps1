$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$exe = Join-Path $repo 'artifacts/windows/MineriaBitcoin.exe'
$app = Start-Process -FilePath $exe -PassThru
try {
    if (!$app.WaitForInputIdle(45000)) { throw 'La ventana no inició a tiempo.' }
    $app.Refresh()
    if ($app.HasExited -or $app.MainWindowHandle -eq 0) { throw 'No se abrió la ventana principal.' }
    $initialCpu = $app.TotalProcessorTime.TotalSeconds
    Start-Sleep -Seconds 3
    $app.Refresh()
    if ($app.HasExited) { throw 'La aplicación terminó inesperadamente.' }
    if ($app.TotalProcessorTime.TotalSeconds - $initialCpu -gt 1.5) {
        throw 'Consumo inesperado al abrir la aplicación sin iniciar minería.'
    }
    if (!$app.CloseMainWindow()) { throw 'No se pudo cerrar la ventana.' }
    if (!$app.WaitForExit(10000)) { throw 'El proceso no terminó después de cerrar.' }
    if ($app.ExitCode -ne 0) { throw "La aplicación terminó con código $($app.ExitCode)." }
    Write-Host 'PASS: ventana abierta en reposo y cierre limpio; no se inició minería.'
} finally {
    if (!$app.HasExited) { $app.Kill(); $app.WaitForExit() }
    $app.Dispose()
}
