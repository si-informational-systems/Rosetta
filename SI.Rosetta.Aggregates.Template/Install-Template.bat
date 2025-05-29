@echo off
echo Installing SI.Rosetta Aggregate Template...

set "TEMPLATE_DIR=%USERPROFILE%\Documents\Visual Studio 2022\Templates\ItemTemplates\FSharp\SI.Rosetta.Aggregate"

if not exist "%USERPROFILE%\Documents\Visual Studio 2022\Templates\ItemTemplates\FSharp\" (
    echo Creating FSharp templates directory...
    mkdir "%USERPROFILE%\Documents\Visual Studio 2022\Templates\ItemTemplates\FSharp"
)

if exist "%TEMPLATE_DIR%" (
    echo Removing existing template...
    rmdir /s /q "%TEMPLATE_DIR%"
)

echo Creating template directory...
mkdir "%TEMPLATE_DIR%"

echo Copying template files...
copy "MyTemplate.vstemplate" "%TEMPLATE_DIR%\"
copy "AggregateState.fs" "%TEMPLATE_DIR%\"
copy "Aggregate.fs" "%TEMPLATE_DIR%\"
copy "AggregateHandler.fs" "%TEMPLATE_DIR%\"

echo.
echo Template installed successfully!
echo Location: %TEMPLATE_DIR%
echo.
echo Please restart Visual Studio to use the template.
echo.
pause 