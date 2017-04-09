rm -rf bin\
dotnet publish -f netcoreapp1.0 -c release
cd bin\release\netcoreapp1.0\publish
7z a -tzip deploy.zip *
