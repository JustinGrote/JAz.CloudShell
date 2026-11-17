@{
    RootModule           = 'JAz.CloudShell.dll'
    ModuleVersion        = '0.1.0'
    GUID                 = '4f6f0b54-7d0e-4d8e-9a39-2f5b7d0a6c11'
    Author               = 'Justin Grote'
    Description          = 'PowerShell remoting (PSSession) transport over the Azure Cloud Shell terminal WebSocket.'
    PowerShellVersion    = '7.6'
    CompatiblePSEditions = @('Core')
    CmdletsToExport      = @('New-JAzCloudShellSession')
    FunctionsToExport    = @()
    AliasesToExport      = @()
    VariablesToExport    = @()
    PrivateData          = @{
        PSData = @{
            Tags = @('Azure', 'CloudShell', 'Remoting', 'PSSession')
        }
    }
}
