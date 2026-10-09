param([string]$Configuration = 'Debug')

$ErrorActionPreference = 'Stop'
$repository = Split-Path $PSScriptRoot -Parent
$assembly = Join-Path $repository "src/PasswordTool.Api/bin/$Configuration/net10.0/PasswordTool.Api.dll"
if (-not (Test-Path -LiteralPath $assembly)) { throw 'Build PasswordTool.Api before running this smoke test.' }

$listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 0)
$listener.Start()
$port = $listener.LocalEndpoint.Port
$listener.Stop()
$origin = "http://127.0.0.1:$port"
$log = Join-Path ([System.IO.Path]::GetTempPath()) "PasswordTool.Api.Security.$([Guid]::NewGuid().ToString('N'))"
$apiProcess = $null
$client = [System.Net.Http.HttpClient]::new()
$client.Timeout = [TimeSpan]::FromSeconds(30)

function Send-Request([string]$Path, [object]$Body, [string]$HostHeader = '', [string]$Raw = '') {
    $request = [System.Net.Http.HttpRequestMessage]::new()
    $request.RequestUri = "$origin/api/password/$Path"
    $request.Method = if ($null -eq $Body -and $Raw -eq '') { [System.Net.Http.HttpMethod]::Get } else { [System.Net.Http.HttpMethod]::Post }
    if ($request.Method -eq [System.Net.Http.HttpMethod]::Post) {
        $json = if ($Raw -ne '') { $Raw } else { $Body | ConvertTo-Json -Compress }
        $request.Content = [System.Net.Http.StringContent]::new($json, [System.Text.Encoding]::UTF8, 'application/json')
    }
    if ($HostHeader -ne '') { $request.Headers.Host = $HostHeader }
    try { return $client.SendAsync($request).GetAwaiter().GetResult() }
    finally { $request.Dispose() }
}

function Assert-Status($Response, [int]$Status, [string]$Case) {
    if ([int]$Response.StatusCode -ne $Status) { throw "$Case returned $([int]$Response.StatusCode), expected $Status." }
}

try {
    $apiProcess = Start-Process -FilePath (Get-Command dotnet).Source -ArgumentList @($assembly, '--urls', $origin) `
        -WindowStyle Hidden -PassThru -RedirectStandardOutput "$log.out" -RedirectStandardError "$log.err" `
        -Environment @{ ASPNETCORE_ENVIRONMENT = 'Development'; ASPNETCORE_HTTPS_PORT = '' }
    $ready = $false
    for ($attempt = 0; $attempt -lt 100; $attempt++) {
        if ($apiProcess.HasExited) { throw "API exited before readiness. Read $log.err." }
        try {
            $response = Send-Request 'algorithms' $null
            $ready = [int]$response.StatusCode -eq 200
            $response.Dispose()
            if ($ready) { break }
        } catch [System.Net.Http.HttpRequestException] { }
        Start-Sleep -Milliseconds 100
    }
    if (-not $ready) { throw 'API did not become ready.' }

    $response = Send-Request 'hash' @{ password = 'test-only password'; algorithmName = 'pbkdf2-sha256' }
    Assert-Status $response 200 'hash'
    if ($response.Headers.CacheControl.NoStore -ne $true) { throw 'Password API responses must disable caching.' }
    $storedHash = ($response.Content.ReadAsStringAsync().GetAwaiter().GetResult() | ConvertFrom-Json).storedHash
    $response.Dispose()
    $response = Send-Request 'verify' @{ password = 'test-only password'; storedHash = $storedHash }
    Assert-Status $response 200 'verify'
    if (($response.Content.ReadAsStringAsync().GetAwaiter().GetResult() | ConvertFrom-Json).isValid -ne $true) { throw 'Generated hash did not verify.' }
    $response.Dispose()

    foreach ($case in @(
        @{ Path = 'hash'; Body = @{ password = 'test-only password'; algorithmName = 'md5' }; Status = 400 },
        @{ Path = 'hash'; Body = @{ password = ('x' * 4097); algorithmName = 'argon2id' }; Status = 400 },
        @{ Path = 'verify'; Body = @{ password = 'test-only password'; storedHash = ('x' * 2049) }; Status = 400 },
        @{ Path = 'inspect'; Body = @{ storedHash = ('x' * 2049) }; Status = 400 }
    )) {
        $response = Send-Request $case.Path $case.Body
        Assert-Status $response $case.Status "$($case.Path) input policy"
        $response.Dispose()
    }
    foreach ($hostileHash in @(
        'PBKDF2-SHA256$v=1$iter=2147483647$salt=AAAAAAAAAAAAAAAAAAAAAA==$hash=AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=',
        'PBKDF2-SHA256$v=1$v=1$iter=210000$salt=AAAAAAAAAAAAAAAAAAAAAA==$hash=AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=',
        'ARGON2ID$v=1$m=2147483647$t=3$p=2$salt=AAAAAAAAAAAAAAAAAAAAAA==$hash=AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=',
        'SCRYPT$v=1$N=16384$r=2147483647$p=2147483647$salt=AAAAAAAAAAAAAAAAAAAAAA==$hash=AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=',
        ('$2a$31$' + ('A' * 53))
    )) {
        $response = Send-Request 'verify' @{ password = 'test-only password'; storedHash = $hostileHash }
        Assert-Status $response 200 'hostile KDF parameters'
        if (($response.Content.ReadAsStringAsync().GetAwaiter().GetResult() | ConvertFrom-Json).isValid -ne $false) { throw 'Hostile hash was accepted.' }
        $response.Dispose()
    }
    $response = Send-Request 'hash' $null -Raw '{'
    Assert-Status $response 400 'malformed JSON'
    $response.Dispose()
    $response = Send-Request 'hash' $null -Raw ('{"password":"' + ('x' * 40000) + '","algorithmName":"argon2id"}')
    Assert-Status $response 413 'oversized request body'
    $response.Dispose()
    $response = Send-Request 'algorithms' $null -HostHeader 'untrusted.example'
    Assert-Status $response 403 'untrusted Host header'
    $response.Dispose()

    $requests = [System.Collections.Generic.List[System.Net.Http.HttpRequestMessage]]::new()
    $tasks = [System.Collections.Generic.List[System.Threading.Tasks.Task[System.Net.Http.HttpResponseMessage]]]::new()
    try {
        for ($index = 0; $index -lt 16; $index++) {
            $request = [System.Net.Http.HttpRequestMessage]::new([System.Net.Http.HttpMethod]::Post, "$origin/api/password/hash")
            $request.Content = [System.Net.Http.StringContent]::new('{"password":"test-only password","algorithmName":"argon2id"}', [System.Text.Encoding]::UTF8, 'application/json')
            $requests.Add($request)
            $tasks.Add($client.SendAsync($request))
        }
        $statuses = @($tasks | ForEach-Object { $response = $_.GetAwaiter().GetResult(); try { [int]$response.StatusCode } finally { $response.Dispose() } })
        if (200 -notin $statuses -or 429 -notin $statuses -or @($statuses | Where-Object { $_ -notin 200, 429 }).Count -ne 0) {
            throw "KDF concurrency limit returned unexpected statuses: $statuses"
        }
    } finally { foreach ($request in $requests) { $request.Dispose() } }

    $limited = $false
    for ($index = 0; $index -lt 61; $index++) {
        $response = Send-Request 'algorithms' $null
        try {
            if ([int]$response.StatusCode -eq 429) { $limited = $true; break }
            Assert-Status $response 200 'rate limit load'
        } finally { $response.Dispose() }
    }
    if (-not $limited) { throw 'Global API request rate limit was not enforced.' }
    Write-Output 'PASS: API roundtrip, no-store, educational policy, length/body limits, malformed input, hostile KDF parameters, Host boundary, concurrent KDF rejection, global rate limit.'
} finally {
    $client.Dispose()
    if ($null -ne $apiProcess -and -not $apiProcess.HasExited) { $apiProcess.Kill(); $apiProcess.WaitForExit() }
    if ($null -ne $apiProcess) { $apiProcess.Dispose() }
    Remove-Item -LiteralPath "$log.out", "$log.err" -ErrorAction SilentlyContinue
}
