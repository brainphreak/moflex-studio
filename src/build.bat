@echo off
rem Builds "Moflex Studio.exe" with the C# compiler that ships with Windows (.NET Framework 4.x) -- nothing to install.
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe
set ICON=
if exist "%~dp0icon.ico" set ICON=/win32icon:"%~dp0icon.ico"
"%CSC%" /nologo /target:winexe /optimize+ /out:"%~dp0..\Moflex Studio.exe" %ICON% ^
  /r:System.Web.Extensions.dll /r:System.IO.Compression.dll /r:System.IO.Compression.FileSystem.dll ^
  /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Core.dll "%~dp0MoflexStudio.cs"
if errorlevel 1 (echo BUILD FAILED & exit /b 1)
echo Built "%~dp0..\Moflex Studio.exe"
