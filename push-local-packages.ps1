# Get all directories in the current location
$directories = Get-ChildItem -Directory

# Initialize an empty array to store the paths of the latest nupkg files
$latestNupkgPaths = @()
# Initialize an array to store successfully pushed packages
$successfulPushes = @()

# Go through each directory
foreach ($dir in $directories) {
    # Check if bin/Debug exists
    $debugPath = Join-Path -Path $dir.FullName -ChildPath "bin\Debug"
    if (Test-Path $debugPath) {
        # Get all nupkg files recursively in the Debug directory
        $nupkgFiles = Get-ChildItem -Path $debugPath -Filter "*.nupkg" -Recurse | 
                      Sort-Object -Property Name -Descending
        
        # If any nupkg files were found, add the first one (latest version) to our list
        if ($nupkgFiles.Count -gt 0) {
            $latestNupkgPaths += $nupkgFiles[0].FullName
        }
    }
}

# Push each package to the local NuGet feed
foreach ($packagePath in $latestNupkgPaths) {
    Write-Host "Pushing package: $packagePath"
    try {
        dotnet nuget push -s "http://localhost:5000/v3/index.json" -k "Mladen" $packagePath
        $successfulPushes += $packagePath
    }
    catch {
        Write-Host "Failed to push package: $packagePath" -ForegroundColor Red
    }
}

# Display summary
Write-Host "`nSuccessfully pushed packages:" -ForegroundColor Green
foreach ($package in $successfulPushes) {
    Write-Host "- $package"
}

Write-Host "`nPress any key to continue..."
$null = $Host.UI.RawUI.ReadKey("NoEcho,IncludeKeyDown")