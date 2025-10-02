Command to build single file for Linux: dotnet publish -c Release -r linux-x64 -p:SelfContained=true -p:PublishSingleFile=true
Command to send binary to the Linux Server: scp bin\Release\net8.0\linux-x64\publish\ServerApp root@192.168.0.45:/root/unity-networking
