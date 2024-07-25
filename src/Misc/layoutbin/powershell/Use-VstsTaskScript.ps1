# A PowerShell script that is used to invoke a VSTS task script. This script is used by the VSTS task runner to invoke the task script.
# This script resplaces some legacy stuff in PowerShell3Handler.cs and turns it into a dedicated function. 
# since it is parameterized it can be signed and trusted for WDAC and CLM.
# This is the original code that has been repalced - 
# @". ([scriptblock]::Create('if ([Console]::InputEncoding -is [Text.UTF8Encoding] -and [Console]::InputEncoding.GetPreamble().Length -ne 0) {{ [Console]::InputEncoding = New-Object Text.UTF8Encoding $false }} if (!$PSHOME) {{ $null = Get-Item -LiteralPath ''variable:PSHOME'' }} else {{ Import-Module -Name ([System.IO.Path]::Combine($PSHOME, ''Modules\Microsoft.PowerShell.Management\Microsoft.PowerShell.Management.psd1'')) ; Import-Module -Name ([System.IO.Path]::Combine($PSHOME, ''Modules\Microsoft.PowerShell.Utility\Microsoft.PowerShell.Utility.psd1'')) }}')) 2>&1 | ForEach-Object {{ Write-Verbose $_.Exception.Message -Verbose }} ; Import-Module -Name '{0}' -ArgumentList @{{ NonInteractive = $true }} -ErrorAction Stop ; $VerbosePreference = '{1}' ; $DebugPreference = '{1}' ; Invoke-VstsTaskScript -ScriptBlock ([scriptblock]::Create('. ''{2}'''))",

# 'System.Culture' task variable: en-us

param ( 
    $name, # Argument 0 
    $Debug, # Argument 1
    $scriptBlockString	# Argument 2 
)

if ([Console]::InputEncoding -is [Text.UTF8Encoding] -and [Console]::InputEncoding.GetPreamble().Length -ne 0) {
    [Console]::InputEncoding = New-Object Text.UTF8Encoding $false 
} 

if (!$PSHOME) { 
    $null = Get-Item -LiteralPath 'variable:PSHOME' 
}
else { 
    Import-Module -Name ([System.IO.Path]::Combine($PSHOME, 'Modules\Microsoft.PowerShell.Management\Microsoft.PowerShell.Management.psd1')) 
    Import-Module -Name ([System.IO.Path]::Combine($PSHOME, 'Modules\Microsoft.PowerShell.Utility\Microsoft.PowerShell.Utility.psd1')) 
}

$importSplat = @{
    Name        = $name 
    ErrorAction = 'Stop'
}

Write-Output "ADO Pipeline Update Test Tim Brigham"

Import-Module @importSplat 
Invoke-VstsTaskScript -ScriptBlock ([scriptblock]::Create( $scriptBlockString ))

