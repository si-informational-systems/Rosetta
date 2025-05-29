Write-Host "Installing SI.Rosetta Aggregate Template..." -ForegroundColor Green

$templateDir = "$env:USERPROFILE\Documents\Visual Studio 2022\Templates\ItemTemplates\FSharp\SI.Rosetta.Aggregate"
$fsharpTemplatesDir = "$env:USERPROFILE\Documents\Visual Studio 2022\Templates\ItemTemplates\FSharp"

# Create FSharp templates directory if it doesn't exist
if (!(Test-Path $fsharpTemplatesDir)) {
    Write-Host "Creating FSharp templates directory..." -ForegroundColor Yellow
    New-Item -ItemType Directory -Path $fsharpTemplatesDir -Force | Out-Null
}

# Remove existing template if it exists
if (Test-Path $templateDir) {
    Write-Host "Removing existing template..." -ForegroundColor Yellow
    Remove-Item -Path $templateDir -Recurse -Force
}

# Create template directory
Write-Host "Creating template directory..." -ForegroundColor Yellow
New-Item -ItemType Directory -Path $templateDir -Force | Out-Null

# Copy template files
Write-Host "Copying template files..." -ForegroundColor Yellow
$currentDir = Get-Location
Copy-Item -Path "$currentDir\MyTemplate.vstemplate" -Destination $templateDir
Copy-Item -Path "$currentDir\AggregateState.fs" -Destination $templateDir
Copy-Item -Path "$currentDir\Aggregate.fs" -Destination $templateDir
Copy-Item -Path "$currentDir\AggregateHandler.fs" -Destination $templateDir

Write-Host ""
Write-Host "Template installed successfully!" -ForegroundColor Green
Write-Host "Location: $templateDir" -ForegroundColor Cyan
Write-Host ""
Write-Host "Please restart Visual Studio to use the template." -ForegroundColor Yellow
Write-Host ""
Read-Host "Press Enter to continue" 