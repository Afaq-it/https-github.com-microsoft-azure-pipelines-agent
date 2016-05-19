@echo off
setlocal
if defined VERBOSE_ARG (
  set VERBOSE_ARG='Continue'
) else (
  set VERBOSE_ARG='SilentlyContinue'
)

powershell.exe -NoLogo -Sta -NoProfile -NonInteractive -ExecutionPolicy Unrestricted -Command "$VerbosePreference = %VERBOSE_ARG% ; Get-ChildItem -LiteralPath '%~dp0' | ForEach-Object { Write-Verbose ('Unblock: {0}' -f $_.FullName) ; $_ } | Unblock-File | Out-Null ; Get-ChildItem -Recurse -LiteralPath '%~dp0bin', '%~dp0externals' | Where-Object { $_ -match '\.(ps1|psd1|psm1)$' } | ForEach-Object { Write-Verbose ('Unblock: {0}' -f $_.FullName) ; $_ } | Unblock-File | Out-Null"

:run
IF "%1"=="remove" (
    "%~dp0bin\Agent.Listener.exe" unconfigure
) ELSE (
    "%~dp0bin\Agent.Listener.exe" configure %*
    IF EXIST .Agent (attrib +h .Agent)
    IF EXIST .Credentials (attrib +h .Credentials)
    IF EXIST .Service (attrib +h .Service)
)

