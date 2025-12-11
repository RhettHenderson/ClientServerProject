
# =============================
# Project paths (new structure)
# =============================
COMMON_PROJ := Libraries/Common/Common.csproj
SERVER_PROJ := Libraries/ServerApp/ServerApp.csproj
CLIENT_PROJ := Libraries/ClientApp/ClientApp.csproj
SERVER_CLI_PROJ := ServerInterfaces/ServerConsole/ServerConsole.csproj
CLIENT_CLI_PROJ := ClientInterfaces/ClientConsole/ClientConsole.csproj

# =============================
# Build settings
# =============================
CONFIG   := Release
OUTDIR   := binaries

# RIDs
WINRID   := win-x64
LINUXRID := linux-x64
MACRID := osx-x64

# =============================
#AOT and single-file build options
# =============================
AOT := --self-contained true \
	-p:PublishAot=true -p:StripSymbols=true -p:DebugType=none

SINGLE := --self-contained true \
	-p:PublishSingleFile=true -p:DebugType=none

FULL := --self-contained true \
	-p:DebugType=none

# Decide which targets "publish" should build
ifeq ($(OS),Windows_NT)
PUBLISH_TARGETS := server-win client-win
else ifeq ($(shell uname -s),Linux)
PUBLISH_TARGETS := server-linux client-linux
else ifeq ($(shell uname -s),Darwin)
PUBLISH_TARGETS := server-mac client-mac
else
@echo "Unknknown operating system. Publish target won't work, you have to select the targets manually"
endif

.PHONY: publish server-win server-win-single client-win client-win-single \
        server-linux server-linux-single client-linux client-linux-single \
        server-mac server-mac-single client-mac client-mac-single

publish: $(PUBLISH_TARGETS)

client-win:
	@echo "== Publishing Client (Windows AOT) =="
	dotnet publish $(CLIENT_CLI_PROJ) -c $(CONFIG) -r $(WINRID) $(AOT) -o $(OUTDIR)/windows-client

client-win-single:
	@echo "== Publishing Client (Windows Single File) =="
	dotnet publish $(CLIENT_CLI_PROJ) -c $(CONFIG) -r $(WINRID) $(SINGLE) -o $(OUTDIR)/windows-client-single

server-win:
	@echo "== Publishing Server (Windows AOT) =="
	dotnet publish $(SERVER_CLI_PROJ) -c $(CONFIG) -r $(WINRID) $(AOT) -o $(OUTDIR)/windows-server

server-win-single:
	@echo "== Publishing Server (Windows Single File) =="
	dotnet publish $(SERVER_CLI_PROJ) -c $(CONFIG) -r $(WINRID) $(SINGLE) -o $(OUTDIR)/windows-server-single

client-linux:
	@echo "== Publishing Client (Linux AOT) =="
	dotnet publish $(CLIENT_CLI_PROJ) -c $(CONFIG) -r $(LINUXRID) $(AOT) -o $(OUTDIR)/linux-client

client-linux-single:
	@echo "== Publishing Client (Linux Single File) =="
	dotnet publish $(CLIENT_CLI_PROJ) -c $(CONFIG) -r $(LINUXRID) $(SINGLE) -o $(OUTDIR)/linux-client-single

server-linux:
	@echo "== Publishing Server (Linux AOT) =="
	dotnet publish $(SERVER_CLI_PROJ) -c $(CONFIG) -r $(LINUXRID) $(AOT) -o $(OUTDIR)/linux-server

server-linux-single:
	@echo "== Publishing Server (Linux Single File) =="
	dotnet publish $(SERVER_CLI_PROJ) -c $(CONFIG) -r $(LINUXRID) $(SINGLE) -o $(OUTDIR)/linux-server-single

server-mac:
	@echo "== Publishing Server (MacOS AOT) =="
	dotnet publish $(SERVER_CLI_PROJ) -c $(CONFIG) -r $(MACRID) $(AOT) -o $(OUTDIR)/macos-server

server-mac-single:
	@echo "== Publishing Server (MacOS single file) =="
	dotnet publish $(SERVER_CLI_PROJ) -c $(CONFIG) -r $(MACRID) $(SINGLE) -o $(OUTDIR)/macos-server-single

client-mac:
	@echo "== Publishing Client (MacOS AOT) =="
	dotnet publish $(CLIENT_CLI_PROJ) -c $(CONFIG) -r $(MACRID) $(AOT) -o $(OUTDIR)/macos-client

client-mac-single:
	@echo "== Publishing Client (MacOS single file) =="
	dotnet publish $(CLIENT_CLI_PROJ) -c $(CONFIG) -r $(MACRID) $(SINGLE) -o $(OUTDIR)/macos-client-single