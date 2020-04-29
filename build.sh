#!/bin/bash
set -ev

dotnet restore ./serilog-sinks-file-archive.sln --runtime netstandard2.0
dotnet build ./src/Serilog.Sinks.File.Archive/Serilog.Sinks.File.Archive.csproj --runtime netstandard2.0 --configuration Release

dotnet test ./test/Serilog.Sinks.File.Archive.Test/Serilog.Sinks.File.Archive.Test.csproj --framework netcoreapp2.2
