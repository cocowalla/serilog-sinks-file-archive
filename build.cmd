dotnet restore .\serilog-sinks-file-archive.sln
dotnet build .\src\Serilog.Sinks.File.Archive\Serilog.Sinks.File.Archive.csproj --configuration Release

dotnet test .\test\Serilog.Sinks.File.Archive.Test\Serilog.Sinks.File.Archive.Test.csproj

dotnet pack .\src\Serilog.Sinks.File.Archive\Serilog.Sinks.File.Archive.csproj -c Release
