[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateScript({ $_.Scheme -eq "https" })]
    [Uri] $BaseUrl
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$passwordPointer = [IntPtr]::Zero
$tokenPointer = [IntPtr]::Zero
$password = $null
$bootstrapToken = $null
$requestBody = $null
$headers = $null
$securePassword = $null
$secureBootstrapToken = $null
$username = $null

try {
    $username = (Read-Host "First administrator username").Trim()
    if ($username.Length -lt 3 -or $username.Length -gt 40) {
        throw "Administrator username must contain 3 to 40 characters."
    }

    $securePassword = Read-Host "First administrator password (at least 12 characters)" -AsSecureString
    $secureBootstrapToken = Read-Host "BOOTSTRAP_TOKEN" -AsSecureString
    $passwordPointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($securePassword)
    $tokenPointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secureBootstrapToken)
    $password = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($passwordPointer)
    $bootstrapToken = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($tokenPointer)

    if ($password.Length -lt 12 -or $password.Length -gt 128) {
        throw "Administrator password must contain 12 to 128 characters."
    }
    if ($bootstrapToken.Length -lt 32) {
        throw "BOOTSTRAP_TOKEN must contain at least 32 characters."
    }

    $endpoint = [Uri]::new($BaseUrl, "/v1/bootstrap/admin")
    $requestBody = @{
        username = $username
        password = $password
    } | ConvertTo-Json -Compress
    $headers = @{ Authorization = "Bootstrap $bootstrapToken" }

    $response = Invoke-RestMethod `
        -Method Post `
        -Uri $endpoint `
        -Headers $headers `
        -ContentType "application/json; charset=utf-8" `
        -Body $requestBody

    Write-Output "Administrator '$($response.user.username)' was created. Delete BOOTSTRAP_TOKEN immediately, then verify login."
}
catch {
    $statusCode = $null
    $responseProperty = $_.Exception.PSObject.Properties["Response"]
    if ($null -ne $responseProperty -and $null -ne $responseProperty.Value) {
        $statusProperty = $responseProperty.Value.PSObject.Properties["StatusCode"]
        if ($null -ne $statusProperty) {
            $statusCode = [int]$statusProperty.Value
        }
    }
    if ($null -ne $statusCode) {
        throw "Administrator bootstrap failed (HTTP $statusCode). Check the Worker URL and one-time token, or verify whether the database already contains a user."
    }
    throw
}
finally {
    $requestBody = $null
    $headers = $null
    $username = $null
    $password = $null
    $bootstrapToken = $null
    $securePassword = $null
    $secureBootstrapToken = $null
    if ($passwordPointer -ne [IntPtr]::Zero) {
        [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($passwordPointer)
    }
    if ($tokenPointer -ne [IntPtr]::Zero) {
        [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($tokenPointer)
    }
}
