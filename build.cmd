@echo off
setlocal
rem Build with the built-in .NET Framework 4.8 compiler (no SDK needed).
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
set WPFDIR=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\WPF
"%CSC%" -nologo -target:winexe -optimize+ -platform:anycpu -out:QuickSidebar.exe ^
 -win32manifest:app.manifest "-lib:%WPFDIR%" ^
 -r:PresentationFramework.dll -r:PresentationCore.dll -r:WindowsBase.dll -r:System.Xaml.dll ^
 -r:System.dll -r:System.Core.dll -r:System.Web.Extensions.dll Program.cs
if errorlevel 1 (
  echo BUILD FAILED
  exit /b 1
)
echo BUILD OK: QuickSidebar.exe
endlocal
