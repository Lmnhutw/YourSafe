[CmdletBinding(DefaultParameterSetName = 'Control')]
param(
    [Parameter(Mandatory, ParameterSetName = 'Control')][ValidateSet('validate', 'validateInteractive', 'signIn', 'enable', 'disable', 'waitApproval', 'waitApprovalGone', 'waitApprovalDeadline', 'assertNoApproval', 'approve', 'cancel', 'lock', 'unlock', 'configureExpiry', 'waitExpired', 'closeDesktop', 'editPassword', 'resetPassword', 'editUrl', 'resetUrl', 'removePassword', 'beginDelete', 'confirmDelete', 'restoreDeleted', 'copyPassword', 'clipboardSetSentinel', 'clipboardAssertSentinel', 'clipboardAssertNumeric', 'clipboardAssertTotpOnly', 'clipboardAssertCurrentOtp', 'clipboardAssertPassword', 'clipboardAssertEmpty', 'clipboardReplaceSame', 'clipboardClearSentinel')][string]$Action,
    [Parameter(Mandatory, ParameterSetName = 'Control')][int]$ProcessId,
    [Parameter(Mandatory, ParameterSetName = 'Control')][string]$TestDirectory,
    [string]$WinAppPath = 'winapp',
    [ValidateRange(1, 80000)][int]$TimeoutMs = 5000,
    [Parameter(Mandatory, ParameterSetName = 'SelfCheck')][switch]$SelfCheck
)

# Only controls the marked synthetic Debug vault; no app launch or lifecycle bypass.
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
function Read-WinAppResult([string]$Verb, [int]$ExitCode, [object[]]$Output) {
    try { $json = ($Output -join [Environment]::NewLine) | ConvertFrom-Json -AsHashtable }
    catch { throw "WinApp $Verb failed ($ExitCode): output is not valid JSON." }
    if ($json -isnot [System.Collections.IDictionary]) { throw "WinApp $Verb failed ($ExitCode): expected a JSON object." }
    if ($json.Contains('error')) {
        # Keep native diagnostics, never include successful UI values or secret arguments.
        $errorInfo = $json['error']
        $reason = if ($errorInfo -is [System.Collections.IDictionary]) { "$($errorInfo['code']): $($errorInfo['message'])" } else { 'WinApp returned an error.' }
        foreach ($secret in @('correct horse battery staple', 'synthetic-test-password', 'changed-synthetic-password', 'YOURSAFE-DISPOSABLE-CLIPBOARD-SENTINEL')) { $reason = $reason.Replace($secret, '[redacted]') }
        $reason = $reason -replace '\b[0-9]{6,8}\b', '[redacted number]'
        $failure = [InvalidOperationException]::new("WinApp $Verb failed ($ExitCode): $($reason.Substring(0, [Math]::Min(1000, $reason.Length)))")
        if ($errorInfo -is [System.Collections.IDictionary]) { $failure.Data['WinAppCode'] = $errorInfo['code'] }
        throw $failure
    }
    $emptySearch = $Verb -eq 'search' -and $json.Contains('matchCount') -and $json.Contains('matches') -and
        $json['matchCount'] -ceq 0 -and $json['matches'] -is [array] -and $json['matches'].Count -eq 0
    if ($Verb -eq 'wait-for') {
        if (-not $json.Contains('timedOut') -or $json['timedOut'] -isnot [bool]) { throw 'WinApp wait-for returned no timeout status.' }
        if ($json['timedOut']) { throw 'UI condition timed out.' }
    }
    if ($ExitCode -ne 0 -and -not ($ExitCode -eq 1 -and $emptySearch)) { throw "WinApp $Verb failed ($ExitCode)." }
    if ($Verb -eq 'search' -and (-not $json.Contains('matchCount') -or -not $json.Contains('matches') -or
        $json['matchCount'] -isnot [long] -and $json['matchCount'] -isnot [int] -or
        $json['matchCount'] -lt 0 -or $json['matches'] -isnot [array] -or $json['matchCount'] -ne $json['matches'].Count)) {
        throw 'WinApp search returned an invalid match result.'
    }
    return $json
}

function SyntheticOtp([long]$UnixSeconds, [bool]$TotpOnly = $false) {
    $period = if ($TotpOnly) { 60 } else { 30 }
    $digits = if ($TotpOnly) { 8 } else { 6 }
    $counter = [BitConverter]::GetBytes([long][Math]::Floor($UnixSeconds / $period))
    [Array]::Reverse($counter)
    # Public fixture secret JBSWY3DPEHPK3PXP, never a real account.
    $key = [Convert]::FromHexString('48656C6C6F21DEADBEEF')
    $hmac = if ($TotpOnly) { [Security.Cryptography.HMACSHA256]::new($key) } else { [Security.Cryptography.HMACSHA1]::new($key) }
    try { $hash = $hmac.ComputeHash($counter) } finally { $hmac.Dispose() }
    $offset = $hash[$hash.Length - 1] -band 15
    $modulus = if ($TotpOnly) { 100000000 } else { 1000000 }
    return ((([long]($hash[$offset] -band 127) -shl 24) -bor ([long]$hash[$offset + 1] -shl 16) -bor ([long]$hash[$offset + 2] -shl 8) -bor $hash[$offset + 3]) % $modulus).ToString("D$digits")
}

function Ui {
    for ($attempt = 0; $attempt -lt 2; $attempt++) {
        $output = & $WinAppPath ui @args -a $ProcessId --json 2>&1
        try { return Read-WinAppResult $args[0] $LASTEXITCODE @($output) }
        catch {
            if ($attempt -eq 0 -and $args[0] -in 'wait-for', 'search', 'get-property', 'get-value' -and $_.Exception.Data['WinAppCode'] -ceq 'stale_element') { continue }
            throw "WinApp $($args[0]) [$($args[1])]: $($_.Exception.Message)"
        }
    }
}

if ($SelfCheck) {
    $cases = @(
        @{ verb = 'search'; code = 1; json = '{"matchCount":0,"matches":[]}'; expected = '' },
        @{ verb = 'search'; code = 0; json = '{"matchCount":1,"matches":[{"automationId":"fixture"}]}'; expected = '' },
        @{ verb = 'wait-for'; code = 0; json = '{"timedOut":false}'; expected = '' },
        @{ verb = 'search'; code = 1; json = '{"error":{"code":"internal_error","message":"Original WinApp failure"}}'; expected = 'Original WinApp failure' },
        @{ verb = 'wait-for'; code = 1; json = '{"error":{"code":"internal_error","message":"Original wait failure"}}'; expected = 'Original wait failure' },
        @{ verb = 'search'; code = 1; json = '{}'; expected = 'failed (1)' },
        @{ verb = 'search'; code = 0; json = '{"matches":[]}'; expected = 'invalid match' },
        @{ verb = 'search'; code = 0; json = '{"matchCount":0,"matches":[{}]}'; expected = 'invalid match' },
        @{ verb = 'wait-for'; code = 0; json = '{"timedOut":true}'; expected = 'timed out' },
        @{ verb = 'wait-for'; code = 1; json = '{"timedOut":true}'; expected = 'timed out' },
        @{ verb = 'wait-for'; code = 0; json = '{}'; expected = 'no timeout status' },
        @{ verb = 'invoke'; code = 0; json = '{"error":{"code":"blocked","message":"synthetic-test-password 123456"}}'; expected = '[redacted]' },
        @{ verb = 'invoke'; code = 0; json = 'not JSON synthetic-test-password'; expected = 'not valid JSON' }
    )
    foreach ($case in $cases) {
        $failure = ''
        try { Read-WinAppResult $case.verb $case.code @($case.json) | Out-Null }
        catch { $failure = $_.Exception.Message }
        if ($case.expected -eq '' -and $failure -ne '' -or $case.expected -ne '' -and -not $failure.Contains($case.expected)) { throw "WinApp result self-check failed: $($case.verb) ($($case.code)): $failure" }
        if ($failure.Contains('synthetic-test-password') -or $failure.Contains('123456')) { throw 'WinApp diagnostics exposed a synthetic secret.' }
    }
    foreach ($vector in @(@{ time = 0; code = '282760' }, @{ time = 59; code = '996554' }, @{ time = 60; code = '602287' })) {
        if ((SyntheticOtp $vector.time) -cne $vector.code) { throw 'Synthetic OTP self-check failed.' }
    }
    function MockWinApp {
        $script:mockCalls++
        if ($script:mockCalls -eq 1 -or $script:mockAlwaysFails) { $global:LASTEXITCODE = 1; return ('{"error":{"code":"' + $script:mockError + '","message":"Mock UI failure"}}') }
        $global:LASTEXITCODE = 0
        return $script:mockSuccess
    }
    $WinAppPath = 'MockWinApp'
    foreach ($verb in @('wait-for', 'search', 'get-property', 'get-value', 'invoke', 'set-value')) {
        $script:mockCalls = 0; $script:mockError = 'stale_element'; $script:mockAlwaysFails = $false
        $script:mockSuccess = if ($verb -eq 'search') { '{"matchCount":0,"matches":[]}' } else { '{"timedOut":false}' }
        $failed = $false
        try { Ui $verb FixtureSelector | Out-Null } catch { $failed = $true }
        $readOnly = $verb -in 'wait-for', 'search', 'get-property', 'get-value'
        if ($script:mockCalls -ne (1 + [int]$readOnly) -or $failed -eq $readOnly) { throw "WinApp retry self-check failed for $verb." }
    }
    foreach ($errorCode in @('stale_element', 'internal_error')) {
        $script:mockCalls = 0; $script:mockError = $errorCode; $script:mockAlwaysFails = $true
        $failed = $false
        try { Ui search FixtureSelector | Out-Null } catch { $failed = $_.Exception.Message.Contains('FixtureSelector') }
        if (-not $failed -or $script:mockCalls -ne (1 + [int]($errorCode -eq 'stale_element'))) { throw 'WinApp bounded retry/error-context self-check failed.' }
    }
    Write-Output "PASS: $($cases.Count) WinApp JSON/exit/timeout/diagnostic checks; no desktop or registration touched."
    Write-Output 'PASS: three synthetic SHA1 OTP vectors, including a period boundary.'
    foreach ($vector in @(@{ time = 0; code = '96023015' }, @{ time = 59; code = '96023015' }, @{ time = 60; code = '36344551' })) {
        if ((SyntheticOtp $vector.time $true) -cne $vector.code) { throw 'Synthetic SHA256 OTP self-check failed.' }
    }
    Write-Output 'PASS: three independently pinned SHA256 eight-digit/sixty-second OTP vectors.'
    Write-Output 'PASS: eight read-only retry/mutation no-retry/error-context checks.'
    exit 0
}

$testRoot = [IO.Path]::TrimEndingDirectorySeparator((Resolve-Path -LiteralPath $TestDirectory).Path)
$process = Get-Process -Id $ProcessId
if ($process.MainWindowHandle -eq [IntPtr]::Zero) { throw 'The supplied process has no main window.' }
$null = Get-Command $WinAppPath -ErrorAction Stop

function UnlockTable {
    Ui wait-for BtnLockVault -p Name --value 'Unlock vault' -t 5000 | Out-Null
    Ui invoke BtnLockVault | Out-Null
    Ui wait-for TxtUnlockMasterPassword -t 5000 | Out-Null
    Ui set-value TxtUnlockMasterPassword 'correct horse battery staple' | Out-Null
    Ui invoke PrimaryButton | Out-Null
    Ui wait-for BtnLockVault -p Name --value 'Lock vault' -t 15000 | Out-Null
    Ui wait-for TxtUnlockMasterPassword --gone -t 5000 | Out-Null
}

function SyntheticRow([string]$Title, [string]$Button) {
    Ui invoke NavVault | Out-Null
    Ui wait-for TxtVaultSearch -t 5000 | Out-Null
    Ui set-value TxtVaultSearch $Title | Out-Null
    Ui wait-for $Title -t 5000 | Out-Null
    $matches = @((Ui search $Button).matches | Where-Object { -not $_.isOffscreen -and $_.automationId -like ($Button + '_*') })
    if ($matches.Count -ne 1) { throw 'The synthetic item must identify exactly one row action.' }
    return $matches[0].automationId
}

function ClipboardProbe {
    if ('AutofillClipboardProbe' -as [type]) { return }
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Text;
public static class AutofillClipboardProbe {
    [DllImport("user32.dll")] static extern IntPtr OpenInputDesktop(uint flags, bool inherit, uint access);
    [DllImport("user32.dll")] static extern bool CloseDesktop(IntPtr desktop);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool GetUserObjectInformationW(IntPtr handle, int index, StringBuilder value, uint length, out uint needed);
    [DllImport("user32.dll")] public static extern IntPtr GetClipboardOwner();
    [DllImport("user32.dll")] public static extern uint GetClipboardSequenceNumber();
    [DllImport("user32.dll")] public static extern bool IsClipboardFormatAvailable(uint format);
    [DllImport("user32.dll", SetLastError = true)] static extern bool OpenClipboard(IntPtr owner);
    [DllImport("user32.dll")] static extern IntPtr GetOpenClipboardWindow();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll")] static extern bool CloseClipboard();
    [DllImport("user32.dll")] static extern bool EmptyClipboard();
    [DllImport("user32.dll")] static extern IntPtr GetClipboardData(uint format);
    [DllImport("user32.dll")] static extern IntPtr SetClipboardData(uint format, IntPtr memory);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr CreateWindowExW(uint exStyle, string cls, string title, uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr parameter);
    [DllImport("user32.dll")] static extern bool DestroyWindow(IntPtr window);
    [DllImport("kernel32.dll")] static extern IntPtr GlobalAlloc(uint flags, UIntPtr bytes);
    [DllImport("kernel32.dll")] static extern IntPtr GlobalLock(IntPtr memory);
    [DllImport("kernel32.dll")] static extern bool GlobalUnlock(IntPtr memory);
    [DllImport("kernel32.dll")] static extern IntPtr GlobalFree(IntPtr memory);
    [DllImport("kernel32.dll")] static extern UIntPtr GlobalSize(IntPtr memory);
    public static bool HasInteractiveDesktop() {
        IntPtr desktop = OpenInputDesktop(0, false, 1);
        if (desktop == IntPtr.Zero) return false;
        try {
            var name = new StringBuilder(256);
            return GetUserObjectInformationW(desktop, 2, name, 512, out _) && name.ToString() == "Default";
        } finally { CloseDesktop(desktop); }
    }
    static bool OpenWithRetry(IntPtr owner) {
        int error = 0;
        for (int attempt = 0; attempt < 10; attempt++) {
            if (OpenClipboard(owner)) return true;
            error = Marshal.GetLastPInvokeError();
            System.Threading.Thread.Sleep(100);
        }
        GetWindowThreadProcessId(GetOpenClipboardWindow(), out uint holder);
        throw new InvalidOperationException($"Clipboard unavailable (Windows error {error}, open holder PID {holder}).");
    }
    public static string Read(IntPtr owner, uint sequence) {
        if (!OpenWithRetry(IntPtr.Zero)) throw new InvalidOperationException("Clipboard is busy.");
        try {
            if (GetClipboardOwner() != owner || GetClipboardSequenceNumber() != sequence)
                throw new InvalidOperationException("Clipboard changed outside the synthetic test.");
            IntPtr data = GetClipboardData(13);
            if (data == IntPtr.Zero) return "";
            ulong size = GlobalSize(data).ToUInt64();
            if (size > 65536 || size % 2 != 0) throw new InvalidOperationException("Unexpected clipboard text size.");
            IntPtr pointer = GlobalLock(data);
            if (pointer == IntPtr.Zero) throw new InvalidOperationException("Clipboard read failed.");
            try { return Marshal.PtrToStringUni(pointer, (int)(size / 2)).Split('\0')[0]; }
            finally { GlobalUnlock(data); }
        } finally { CloseClipboard(); }
    }
    public static uint Write(string value) => WriteIfOwned(value, IntPtr.Zero, null);
    public static uint ReplaceOwned(string value, IntPtr owner, uint sequence) => WriteIfOwned(value, owner, sequence);
    static uint WriteIfOwned(string value, IntPtr expectedOwner, uint? expectedSequence) {
        IntPtr window = CreateWindowExW(0, "STATIC", "YourSafe synthetic clipboard test", 0, 0, 0, 0, 0, new IntPtr(-3), IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        if (window == IntPtr.Zero) throw new InvalidOperationException("Test clipboard owner creation failed.");
        uint sequence = 0;
        try {
            if (!OpenWithRetry(window)) throw new InvalidOperationException("Clipboard is busy.");
            try {
                if (expectedSequence.HasValue && (GetClipboardOwner() != expectedOwner || GetClipboardSequenceNumber() != expectedSequence.Value))
                    throw new InvalidOperationException("Clipboard changed outside the synthetic test.");
                if (!EmptyClipboard()) throw new InvalidOperationException("Clipboard replacement failed.");
                if (value != null) {
                    byte[] bytes = Encoding.Unicode.GetBytes(value + "\0");
                    IntPtr memory = GlobalAlloc(0x42, new UIntPtr((uint)bytes.Length));
                    if (memory == IntPtr.Zero) throw new InvalidOperationException("Clipboard allocation failed.");
                    try {
                        IntPtr pointer = GlobalLock(memory);
                        if (pointer == IntPtr.Zero) throw new InvalidOperationException("Clipboard allocation lock failed.");
                        try { Marshal.Copy(bytes, 0, pointer, bytes.Length); } finally { GlobalUnlock(memory); }
                        if (SetClipboardData(13, memory) == IntPtr.Zero) throw new InvalidOperationException("Clipboard write failed.");
                        memory = IntPtr.Zero;
                    } finally { if (memory != IntPtr.Zero) GlobalFree(memory); Array.Clear(bytes, 0, bytes.Length); }
                }
            } finally { CloseClipboard(); }
            sequence = GetClipboardSequenceNumber();
        } finally { DestroyWindow(window); }
        if (GetClipboardOwner() != IntPtr.Zero || GetClipboardSequenceNumber() != sequence)
            throw new InvalidOperationException("Clipboard changed after test replacement.");
        return sequence;
    }
    public static void ClearIfOwned(IntPtr owner, uint sequence) {
        OpenWithRetry(IntPtr.Zero);
        try {
            if (GetClipboardOwner() != owner || GetClipboardSequenceNumber() != sequence)
                throw new InvalidOperationException("Clipboard changed outside the synthetic test.");
            if (!EmptyClipboard()) throw new InvalidOperationException("Clipboard clear failed.");
        } finally { CloseClipboard(); }
    }
}
'@
}

function ClipboardHash([string]$Text) {
    return [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($Text)))
}

try {
    # Fail closed before mutation or focus changes; a real vault has no marker.
    Ui wait-for YourSafeDisposableUiTest -p HelpText --value $testRoot -t 5000 | Out-Null
    if ((Ui get-property YourSafeDisposableUiTest -p HelpText).properties.HelpText -ne $testRoot) {
        throw 'Disposable vault marker mismatch.'
    }
    switch ($Action) {
        'validateInteractive' {
            ClipboardProbe
            if (-not [AutofillClipboardProbe]::HasInteractiveDesktop()) {
                throw 'Unlock Windows before running the popup and clipboard campaign.'
            }
        }
        'signIn' {
            if (@((Ui search 'Digit 1 of 6').matches | Where-Object { -not $_.isOffscreen }).Count -ne 0 -and
                (Ui search TxtMasterPassword).matchCount -ne 0) {
                Ui invoke CloseButton | Out-Null
                Ui wait-for 'Digit 1 of 6' --gone -t 5000 | Out-Null
            }
            if ((Ui search TxtMasterPassword).matchCount -eq 0) {
                Ui invoke NavVault | Out-Null
                Ui wait-for BtnLockVault -t 5000 | Out-Null
                if ((Ui get-property BtnLockVault -p Name).properties.Name -eq 'Unlock vault') { UnlockTable }
                Ui wait-for BtnLockVault -p Name --value 'Lock vault' -t 15000 | Out-Null
                break
            }
            Ui wait-for TxtMasterPassword -t 5000 | Out-Null
            $needsOtp = (Ui get-property BtnUnlock -p Name).properties.Name -eq 'Sign in'
            Ui set-value TxtMasterPassword 'correct horse battery staple' | Out-Null
            Ui invoke BtnUnlock | Out-Null
            if ($needsOtp) {
                Ui wait-for 'Digit 1 of 6' -t 10000 | Out-Null
                $otp = SyntheticOtp ([DateTimeOffset]::UtcNow.ToUnixTimeSeconds())
                for ($index = 0; $index -lt 6; $index++) {
                    Ui set-value ('Digit ' + ($index + 1) + ' of 6') $otp[$index].ToString() | Out-Null
                }
                Ui wait-for PrimaryButton -p IsEnabled --value True -t 5000 | Out-Null
                Ui invoke PrimaryButton | Out-Null
                Ui wait-for 'Digit 1 of 6' --gone -t 15000 | Out-Null
            }
            Ui wait-for BtnLockVault -p Name --value 'Lock vault' -t 15000 | Out-Null
        }
        { $_ -in 'enable', 'disable' } {
            Ui invoke NavSettings | Out-Null
            Ui wait-for TglBrowserIntegration -t 5000 | Out-Null
            Ui scroll-into-view TglBrowserIntegration | Out-Null
            $expected = if ($Action -eq 'enable') { 'On' } else { 'Off' }
            $actual = (Ui get-value TglBrowserIntegration).text
            if ($actual -notin 'On', 'Off') { throw 'Unknown browser integration toggle state.' }
            if ($actual -ne $expected) { Ui invoke TglBrowserIntegration | Out-Null }
            Ui wait-for TglBrowserIntegration --value $expected -t 5000 | Out-Null
        }
        { $_ -in 'waitApproval', 'approve', 'cancel' } {
            Ui wait-for TxtAutofillApprovalDetails -t 10000 | Out-Null
            if ($Action -ne 'waitApproval') {
                $button = if ($Action -eq 'approve') { 'PrimaryButton' } else { 'CloseButton' }
                Ui focus $button | Out-Null
                Ui invoke $button | Out-Null
                Ui wait-for TxtAutofillApprovalDetails --gone -t 5000 | Out-Null
            }
        }
        'waitApprovalGone' { Ui wait-for TxtAutofillApprovalDetails --gone -t $TimeoutMs | Out-Null }
        'waitApprovalDeadline' { Ui wait-for TxtAutofillApprovalDetails --gone -t 80000 | Out-Null }
        'assertNoApproval' {
            if (@((Ui search TxtAutofillApprovalDetails).matches | Where-Object { -not $_.isOffscreen }).Count -ne 0) {
                throw 'An unexpected desktop approval is still visible.'
            }
        }
        'lock' {
            if ((Ui search BtnLockVault).matchCount -eq 0) { Ui invoke NavVault | Out-Null }
            Ui wait-for BtnLockVault -p Name --value 'Lock vault' -t 5000 | Out-Null
            Ui wait-for BtnLockVault -p IsEnabled --value True -t 1000 | Out-Null
            Ui invoke BtnLockVault | Out-Null
            Ui wait-for BtnLockVault -p Name --value 'Unlock vault' -t 5000 | Out-Null
        }
        'unlock' { UnlockTable }
        'configureExpiry' {
            Ui invoke NavSettings | Out-Null
            Ui wait-for CmbVaultDuration -t 5000 | Out-Null
            Ui scroll-into-view CmbVaultDuration | Out-Null
            Ui invoke CmbVaultDuration | Out-Null
            Ui wait-for '1 minute' -t 5000 | Out-Null
            Ui invoke '1 minute' | Out-Null
            Ui wait-for CmbVaultDuration --value '1 minute' -t 5000 | Out-Null
            Ui set-value 'Master Password for settings' 'correct horse battery staple' | Out-Null
            Ui invoke 'Save security settings' | Out-Null
            Ui wait-for 'Vault timeout saved. Restart YourSafe to apply it.' -t 15000 | Out-Null
            Ui invoke CloseButton | Out-Null
            Ui wait-for CloseButton --gone -t 5000 | Out-Null
            Ui invoke NavVault | Out-Null
            # The application caches this setting; a fresh process is required for the one-minute campaign.
        }
        'waitExpired' {
            Ui wait-for BtnLockVault -p Name --value 'Unlock vault' -t 75000 | Out-Null
            Ui wait-for TxtAutofillApprovalDetails --gone -t 5000 | Out-Null
        }
        'closeDesktop' {
            if (-not $process.CloseMainWindow()) { throw 'The marked desktop refused a normal window close.' }
            if (-not $process.WaitForExit(15000)) { throw 'The marked desktop did not exit after a normal window close.' }
        }
        { $_ -in 'editPassword', 'resetPassword', 'editUrl', 'resetUrl', 'removePassword' } {
            $id = SyntheticRow 'TOTP password account' 'Edit'
            Ui invoke $id | Out-Null
            Ui wait-for TxtEditorTitle --value 'TOTP password account' -t 10000 | Out-Null
            if ($Action -in 'editUrl', 'resetUrl') {
                $urlRecord = Join-Path $testRoot 'ui-results/synthetic-item-url.json'
                if ($Action -eq 'editUrl') {
                    $originalUrl = (Ui get-property TxtEditorUrl -p Value).properties.Value
                    $uri = [Uri]$originalUrl
                    if (-not $uri.IsLoopback -or $uri.Scheme -ne 'http') { throw 'Expected only the seeded synthetic loopback URL.' }
                    @{ url = $originalUrl } | ConvertTo-Json | Set-Content -LiteralPath $urlRecord -Encoding utf8
                    $url = 'https://untrusted.example/changed'
                } else { $url = (Get-Content -Raw -LiteralPath $urlRecord | ConvertFrom-Json).url }
                Ui set-value TxtEditorUrl $url | Out-Null
            } else {
                $value = if ($Action -eq 'editPassword') { 'changed-synthetic-password' } elseif ($Action -eq 'removePassword') { '' } else { 'synthetic-test-password' }
                Ui set-value TxtEditorPassword $value | Out-Null
            }
            Ui invoke BtnSaveItem | Out-Null
            Ui wait-for TxtEditorTitle --gone -t 15000 | Out-Null
            Ui wait-for TxtAutofillApprovalDetails --gone -t 5000 | Out-Null
        }
        'beginDelete' {
            $id = SyntheticRow 'Autofill password only' 'More'
            Ui invoke $id | Out-Null
            Ui wait-for Delete -t 5000 | Out-Null
            Ui invoke Delete | Out-Null
            Ui wait-for PrimaryButton -p Name --value 'Move to Trash' -t 5000 | Out-Null
        }
        'confirmDelete' {
            Ui wait-for PrimaryButton -p Name --value 'Move to Trash' -t 5000 | Out-Null
            Ui invoke PrimaryButton | Out-Null
            Ui wait-for PrimaryButton --gone -t 10000 | Out-Null
        }
        'restoreDeleted' {
            Ui invoke NavTrash | Out-Null
            Ui wait-for 'Autofill password only' -t 5000 | Out-Null
            $items = @((Ui search 'Autofill password only').matches | Where-Object { -not $_.isOffscreen -and $_.type -in 'ListItem', 'DataItem' })
            if ($items.Count -ne 1) { throw 'Expected exactly one synthetic Trash item.' }
            Ui invoke $items[0].selector | Out-Null
            Ui invoke 'Restore selected item' | Out-Null
            Ui wait-for 'Autofill password only' --gone -t 10000 | Out-Null
            Ui invoke NavVault | Out-Null
        }
        'copyPassword' {
            $id = SyntheticRow 'Autofill password only' 'CopyPassword'
            Ui invoke $id | Out-Null
        }
        { $_ -like 'clipboard*' } {
            ClipboardProbe
            if (-not [AutofillClipboardProbe]::HasInteractiveDesktop()) { throw 'The interactive desktop is unavailable; unlock Windows.' }
            $recordPath = Join-Path $testRoot 'ui-results/clipboard-test-writer.json'
            switch ($Action) {
                { $_ -in 'clipboardSetSentinel', 'clipboardReplaceSame' } {
                    $text = 'YOURSAFE-DISPOSABLE-CLIPBOARD-SENTINEL'
                    if ($Action -eq 'clipboardReplaceSame') {
                        $expectedSequence = [AutofillClipboardProbe]::GetClipboardSequenceNumber()
                        $text = [AutofillClipboardProbe]::Read($process.MainWindowHandle, $expectedSequence)
                        if ($text.Length -eq 0) { throw 'No synthetic app clipboard value to replace.' }
                    }
                    $hash = ClipboardHash $text
                    $sequence = if ($Action -eq 'clipboardReplaceSame') { [AutofillClipboardProbe]::ReplaceOwned($text, $process.MainWindowHandle, $expectedSequence) }
                        else { [AutofillClipboardProbe]::Write($text) }
                    New-Item -ItemType Directory -Force -Path (Split-Path $recordPath) | Out-Null
                    @{ sequence = $sequence; hash = $hash } | ConvertTo-Json | Set-Content -LiteralPath $recordPath -Encoding utf8
                    $text = $null
                }
                { $_ -in 'clipboardAssertSentinel', 'clipboardClearSentinel' } {
                    $record = Get-Content -Raw -LiteralPath $recordPath | ConvertFrom-Json
                    $text = [AutofillClipboardProbe]::Read([IntPtr]::Zero, [uint32]$record.sequence)
                    if ((ClipboardHash $text) -ne $record.hash) { throw 'The test clipboard replacement was changed or cleared.' }
                    $text = $null
                    if ($Action -eq 'clipboardClearSentinel') { [AutofillClipboardProbe]::ClearIfOwned([IntPtr]::Zero, [uint32]$record.sequence) }
                }
                { $_ -in 'clipboardAssertNumeric', 'clipboardAssertTotpOnly', 'clipboardAssertCurrentOtp' } {
                    $text = [AutofillClipboardProbe]::Read($process.MainWindowHandle, [AutofillClipboardProbe]::GetClipboardSequenceNumber())
                    $now = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()
                    # Copy can cross a period boundary before the probe runs; accept only this fixture's current/previous code.
                    $totpOnly = $Action -eq 'clipboardAssertTotpOnly'
                    $period = if ($totpOnly) { 60 } else { 30 }
                    $expected = @((SyntheticOtp $now $totpOnly))
                    if ($Action -ne 'clipboardAssertCurrentOtp') { $expected += SyntheticOtp ($now - $period) $totpOnly }
                    if ($text -cnotin $expected) { throw 'Clipboard does not match the selected synthetic account OTP.' }
                    $text = $null
                }
                'clipboardAssertPassword' {
                    $text = [AutofillClipboardProbe]::Read($process.MainWindowHandle, [AutofillClipboardProbe]::GetClipboardSequenceNumber())
                    if ($text -ne 'synthetic-test-password') { throw 'Expected the synthetic desktop password.' }
                    $text = $null
                }
                'clipboardAssertEmpty' {
                    if ([AutofillClipboardProbe]::IsClipboardFormatAvailable(13)) { throw 'Clipboard still has Unicode text.' }
                }
            }
        }
    }
    @{ action = $Action; status = 'verified'; processId = $ProcessId; testDirectory = $testRoot } | ConvertTo-Json -Compress
}
catch {
    # Modal UI can prevent normal lock/settings interaction; report that boundary.
    @{ action = $Action; status = 'blocked'; processId = $ProcessId; reason = $_.Exception.Message } | ConvertTo-Json -Compress
    exit 2
}
