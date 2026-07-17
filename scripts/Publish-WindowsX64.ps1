[CmdletBinding()]
param(
    [switch]$SkipTests,
    [switch]$SkipLaunchVerification
)

$ErrorActionPreference = 'Stop'

function Assert-RequiredFiles {
    param(
        [Parameter(Mandatory = $true)]
        [string]$DirectoryPath
    )

    $requiredFiles = @(
        'LanyuePDF.exe',
        'LocalPdfReader.PdfWorker.exe',
        'LanyuePDF.runtimeconfig.json',
        'LocalPdfReader.PdfWorker.runtimeconfig.json',
        'hostfxr.dll',
        'pdfium.dll',
        'e_sqlite3.dll',
        'Microsoft.Data.Sqlite.dll'
    )

    foreach ($relativePath in $requiredFiles) {
        $fullPath = Join-Path $DirectoryPath $relativePath
        if (-not (Test-Path -LiteralPath $fullPath -PathType Leaf)) {
            throw "The publish package is missing required file: $relativePath"
        }
    }
}

function Assert-NoSensitiveFiles {
    param(
        [Parameter(Mandatory = $true)]
        [string]$DirectoryPath
    )

    $forbiddenNames = @(
        '*.key',
        '*.pfx',
        '*.p12',
        '*.pem',
        '*.secrets.json',
        'secrets.json',
        'settings.json',
        'settings.*.json',
        'settings*.backup.json',
        'reader.db',
        '*.db',
        '*.db-wal',
        '*.db-shm',
        '*.sqlite',
        '*.sqlite3',
        '*.log',
        '*.pdb'
    )
    $forbiddenFiles = foreach ($pattern in $forbiddenNames) {
        Get-ChildItem -LiteralPath $DirectoryPath -Recurse -File -Filter $pattern
    }
    $forbiddenDirectories = Get-ChildItem -LiteralPath $DirectoryPath -Recurse -Directory | Where-Object {
        $_.Name -in @('logs', 'credentials')
    }

    if ($forbiddenFiles -or $forbiddenDirectories) {
        $paths = @($forbiddenFiles.FullName) + @($forbiddenDirectories.FullName)
        $names = ($paths | Sort-Object -Unique) -join [Environment]::NewLine
        throw "The publish package contains forbidden user data, secrets, symbols, or logs:$([Environment]::NewLine)$names"
    }
}

function Get-RelativeFilePath {
    param(
        [Parameter(Mandatory = $true)]
        [string]$BaseDirectory,
        [Parameter(Mandatory = $true)]
        [string]$FilePath
    )

    $separator = [System.IO.Path]::DirectorySeparatorChar
    $baseUri = [Uri]($BaseDirectory.TrimEnd($separator) + $separator)
    $fileUri = [Uri]$FilePath
    return [Uri]::UnescapeDataString($baseUri.MakeRelativeUri($fileUri).ToString()).Replace('/', $separator)
}

function Get-PackageInventory {
    param(
        [Parameter(Mandatory = $true)]
        [string]$DirectoryPath
    )

    return @(Get-ChildItem -LiteralPath $DirectoryPath -Recurse -File | Sort-Object FullName | ForEach-Object {
        [ordered]@{
            path = Get-RelativeFilePath -BaseDirectory $DirectoryPath -FilePath $_.FullName
            sizeBytes = $_.Length
            sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
        }
    })
}

function Get-VerificationWorkerProcesses {
    param(
        [Parameter(Mandatory = $true)]
        [string]$VerificationDirectory
    )

    return @([System.Diagnostics.Process]::GetProcessesByName('LocalPdfReader.PdfWorker') | Where-Object {
        try {
            $_.MainModule.FileName.StartsWith(
                $VerificationDirectory,
                [System.StringComparison]::OrdinalIgnoreCase)
        }
        catch {
            $false
        }
    })
}

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$solutionPath = Join-Path $repositoryRoot 'LocalPdfReader.sln'
$artifactsRoot = [System.IO.Path]::GetFullPath((Join-Path $repositoryRoot 'artifacts'))
$publishDirectory = [System.IO.Path]::GetFullPath((Join-Path $artifactsRoot 'publish\win-x64'))
$verificationRoot = [System.IO.Path]::GetFullPath((Join-Path $artifactsRoot 'verify\v1.0-win-x64'))
$verificationDirectory = Join-Path $verificationRoot 'app'
$verificationUserDataDirectory = Join-Path $verificationRoot 'user-data'
$buildPropertiesPath = Join-Path $repositoryRoot 'Directory.Build.props'
$buildProperties = New-Object System.Xml.XmlDocument
$buildProperties.Load($buildPropertiesPath)
$productName = [string]$buildProperties.Project.PropertyGroup.Product
$version = [string]$buildProperties.Project.PropertyGroup.Version

if ([string]::IsNullOrWhiteSpace($productName)) {
    throw 'Directory.Build.props does not define the product name.'
}

if ([string]::IsNullOrWhiteSpace($version)) {
    throw 'Directory.Build.props does not define the product Version.'
}

$archiveName = "LanyuePDF-$version-win-x64.zip"
$archivePath = [System.IO.Path]::GetFullPath((Join-Path $artifactsRoot $archiveName))
$hashPath = "$archivePath.sha256"
$manifestPath = Join-Path $artifactsRoot 'release-manifest.json'
$expectedPrefix = $artifactsRoot.TrimEnd([System.IO.Path]::DirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar

foreach ($pathToValidate in @($publishDirectory, $verificationRoot, $archivePath, $hashPath, $manifestPath)) {
    if (-not $pathToValidate.StartsWith($expectedPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to modify a release path outside the artifacts directory: $pathToValidate"
    }
}

foreach ($directoryPath in @($publishDirectory, $verificationRoot)) {
    if (Test-Path -LiteralPath $directoryPath) {
        Remove-Item -LiteralPath $directoryPath -Recurse -Force
    }
}

foreach ($outputPath in @($archivePath, $hashPath, $manifestPath)) {
    if (Test-Path -LiteralPath $outputPath) {
        Remove-Item -LiteralPath $outputPath -Force
    }
}

if (-not $SkipTests) {
    & dotnet restore $solutionPath
    if ($LASTEXITCODE -ne 0) {
        throw "Solution restore failed with exit code $LASTEXITCODE."
    }

    & dotnet test $solutionPath -c Release --no-restore
    if ($LASTEXITCODE -ne 0) {
        throw "Release tests failed with exit code $LASTEXITCODE."
    }
}

$buildCommit = (& git -C $repositoryRoot rev-parse --short=12 HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($buildCommit)) {
    throw 'Unable to determine the build commit.'
}
$buildInputPaths = @(
    'Directory.Build.props',
    'Directory.Build.targets',
    'Directory.Packages.props',
    'global.json',
    'NuGet.config',
    'LocalPdfReader.sln',
    'src',
    'tests',
    'scripts',
    'release'
)
$workingTreeState = & git -C $repositoryRoot status --porcelain --untracked-files=all -- $buildInputPaths
$isDirtyBuild = [bool]$workingTreeState
if ($isDirtyBuild) {
    $buildCommit = "$buildCommit-dirty"
}
$buildTimestampUtc = [DateTimeOffset]::UtcNow.ToString('O')

$projectPath = Join-Path $repositoryRoot 'src\LocalPdfReader.App\LocalPdfReader.App.csproj'
& dotnet publish $projectPath -c Release -p:PublishProfile=WindowsX64 -p:DebugType=None -p:DebugSymbols=false "-p:BuildCommit=$buildCommit" "-p:BuildTimestampUtc=$buildTimestampUtc"
if ($LASTEXITCODE -ne 0) {
    throw "Windows x64 publish failed with exit code $LASTEXITCODE."
}

Assert-RequiredFiles -DirectoryPath $publishDirectory
Assert-NoSensitiveFiles -DirectoryPath $publishDirectory
$packageInventory = Get-PackageInventory -DirectoryPath $publishDirectory

Compress-Archive -Path (Join-Path $publishDirectory '*') -DestinationPath $archivePath -CompressionLevel Optimal

$archiveHash = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash
$archiveFile = Get-Item -LiteralPath $archivePath
$archiveSizeMiB = [Math]::Round($archiveFile.Length / 1MB, 2)
Set-Content -LiteralPath $hashPath -Encoding utf8 -Value "$archiveHash  $archiveName"

New-Item -ItemType Directory -Path $verificationDirectory -Force | Out-Null
Expand-Archive -LiteralPath $archivePath -DestinationPath $verificationDirectory -Force
Assert-RequiredFiles -DirectoryPath $verificationDirectory
Assert-NoSensitiveFiles -DirectoryPath $verificationDirectory

$extractedInventory = Get-PackageInventory -DirectoryPath $verificationDirectory
if ($extractedInventory.Count -ne $packageInventory.Count) {
    throw "Extracted file count $($extractedInventory.Count) does not match published file count $($packageInventory.Count)."
}

$extractedByPath = @{}
foreach ($file in $extractedInventory) {
    $extractedByPath[$file.path] = $file
}
foreach ($file in $packageInventory) {
    if (-not $extractedByPath.ContainsKey($file.path)) {
        throw "The extracted archive is missing file: $($file.path)"
    }

    $extracted = $extractedByPath[$file.path]
    if ($extracted.sizeBytes -ne $file.sizeBytes -or $extracted.sha256 -ne $file.sha256) {
        throw "The extracted archive file does not match the published file: $($file.path)"
    }
}

$launchVerified = $false
if (-not $SkipLaunchVerification) {
    New-Item -ItemType Directory -Path $verificationUserDataDirectory -Force | Out-Null
    $applicationPath = Join-Path $verificationDirectory 'LanyuePDF.exe'
    $invalidDotnetRoot = Join-Path $verificationRoot 'no-system-dotnet-runtime'
    $startInfo = New-Object System.Diagnostics.ProcessStartInfo
    $startInfo.FileName = $applicationPath
    $startInfo.WorkingDirectory = $verificationDirectory
    $startInfo.UseShellExecute = $false
    $startInfo.WindowStyle = [System.Diagnostics.ProcessWindowStyle]::Hidden
    $startInfo.EnvironmentVariables['DOTNET_ROOT'] = $invalidDotnetRoot
    $startInfo.EnvironmentVariables['DOTNET_ROOT_X64'] = $invalidDotnetRoot
    $startInfo.EnvironmentVariables['DOTNET_MULTILEVEL_LOOKUP'] = '0'
    $startInfo.EnvironmentVariables['LOCALAPPDATA'] = $verificationUserDataDirectory
    $startInfo.EnvironmentVariables['APPDATA'] = $verificationUserDataDirectory
    $applicationProcess = $null
    $verificationWorkers = @()

    try {
        $applicationProcess = [System.Diagnostics.Process]::Start($startInfo)
        if ($null -eq $applicationProcess) {
            throw 'The extracted application process could not be started.'
        }

        $workerDeadline = [DateTimeOffset]::UtcNow.AddSeconds(10)
        do {
            if ($applicationProcess.HasExited) {
                throw "The extracted application exited during startup with code $($applicationProcess.ExitCode)."
            }

            $verificationWorkers = Get-VerificationWorkerProcesses -VerificationDirectory $verificationDirectory
            if ($verificationWorkers.Count -gt 0) {
                break
            }

            Start-Sleep -Milliseconds 250
        } while ([DateTimeOffset]::UtcNow -lt $workerDeadline)

        if ($verificationWorkers.Count -eq 0) {
            throw 'The extracted application stayed open, but its PDF worker did not start.'
        }

        $launchVerified = $true
    }
    finally {
        if ($null -ne $applicationProcess -and -not $applicationProcess.HasExited) {
            $null = $applicationProcess.CloseMainWindow()
            if (-not $applicationProcess.WaitForExit(5000)) {
                $applicationProcess.Kill()
                $applicationProcess.WaitForExit()
            }
        }

        foreach ($workerProcess in (Get-VerificationWorkerProcesses -VerificationDirectory $verificationDirectory)) {
            if (-not $workerProcess.HasExited) {
                $workerProcess.Kill()
                $workerProcess.WaitForExit()
            }
        }
    }
}

[ordered]@{
    product = $productName
    version = $version
    runtime = 'win-x64'
    selfContained = $true
    commit = $buildCommit
    dirtyBuild = $isDirtyBuild
    buildTimeUtc = $buildTimestampUtc
    archive = $archiveName
    sizeBytes = $archiveFile.Length
    sha256 = $archiveHash
    fileCount = $packageInventory.Count
    tests = if ($SkipTests) { 'skipped' } else { 'passed' }
    extractedArchiveVerified = $true
    isolatedLaunchVerified = $launchVerified
    files = $packageInventory
} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $manifestPath -Encoding utf8

Write-Host "Publish package: $archivePath"
Write-Host "Checksum file: $hashPath"
Write-Host "Release manifest: $manifestPath"
Write-Host "Extracted verification directory: $verificationDirectory"
Write-Host "Size: $archiveSizeMiB MiB"
Write-Host "Files: $($packageInventory.Count)"
Write-Host "SHA256: $archiveHash"
Write-Host "Tests: $(if ($SkipTests) { 'skipped' } else { 'passed' })"
Write-Host "Isolated launch: $(if ($SkipLaunchVerification) { 'skipped' } else { 'passed' })"
