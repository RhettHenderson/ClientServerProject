
# =============================
# Project paths (new structure)
# =============================
COMMON_PROJ := Libraries/Common/Common.csproj
SERVER_PROJ := Libraries/ServerApp/ServerApp.csproj
CLIENT_PROJ := Libraries/ClientApp/ClientApp.csproj
SERVER_CLI_PROJ := ServerInterfaces/CLI/CLI.csproj
CLIENT_CLI_PROJ := ClientInterfaces/CLI/CLI.csproj

# =============================
# Build settings
# =============================
CONFIG   := Release
OUTDIR   := binaries

# RIDs
WINRID   := win-x64
LINUXRID := linux-x64

# =============================
#AOT and single-file build options
# =============================
AOT := --self-contained true \
	-p:PublishAot=true -p:StripSymbols=true -p:DebugType=none \

SINGLE := --self-contained true \
	-p:PublishSingleFile=true -p:DebugType=none \

FULL := --self-contained true \
	-p:DebugType=none

# -----------------------------
# Top-level targets
# -----------------------------
.PHONY: all clean tree libraries \
        build-server-cli publish-cli-win publish-cli-linux publish-cli-win-single publish-cli-linux-single \
        publish-cli-win-aot publish-cli-linux-aot \
        build-server publish-server-win publish-server-linux publish-server-win-single publish-server-linux-single \
        publish-server-win-aot publish-server-linux-aot

all: libraries build-server-cli build-client-cli
publish: client-cli-win server-cli-win client-cli-linux server-cli-linux
full: client-cli-win-full server-cli-win-full client-cli-linux-full server-cli-linux-full

# -----------------------------
# Libraries (build only; no publish)
# -----------------------------
libraries: common serverapp clientapp

common:
	@echo.
	@echo == Building Common ==
	dotnet build $(COMMON_PROJ) -c $(CONFIG)

serverapp:
	@echo. 
	@echo == Building ServerApp ==
	dotnet build $(SERVER_PROJ) -c $(CONFIG)

clientapp:
	@echo. 
	@echo == Building ClientApp ==
	dotnet build $(CLIENT_PROJ) -c $(CONFIG)

# -----------------------------
# Interfaces (build only; no publish)
# -----------------------------
interfaces: build-server-cli build-client-cli

build-server-cli:
	@echo.
	@echo == Building Server CLI ==
	dotnet build $(SERVER_CLI_PROJ) -c $(CONFIG)

build-client-cli:
	@echo. 
	@echo == Building Client CLI ==
	dotnet build $(CLIENT_CLI_PROJ) -c $(CONFIG)

#For Windows I only publish the single file and AOT versions
#For Linux, I only periodically update the AOT publish because I have to do it from my server
# -----------------------------
# Interfaces single file and AOT publish
# -----------------------------
client-win:
	@echo. 
	@echo == Publishing Client (Windows AOT) ==
	dotnet publish $(CLIENT_CLI_PROJ) -c $(CONFIG) -r $(WINRID) $(AOT) -o $(OUTDIR)/windows-client

client-win-single:
	@echo. 
	@echo == Publishing Client (Windows Single File) ==
	dotnet publish $(CLIENT_CLI_PROJ) -c $(CONFIG) -r $(WINRID) $(SINGLE) -o $(OUTDIR)/windows-client-single

server-win:
	@echo. 
	@echo == Publishing Server (Windows AOT) ==
	dotnet publish $(SERVER_CLI_PROJ) -c $(CONFIG) -r $(WINRID) $(AOT) -o $(OUTDIR)/windows-server

server-win-single:
	@echo. 
	@echo == Publishing Server (Windows Single File) ==
	dotnet publish $(SERVER_CLI_PROJ) -c $(CONFIG) -r $(WINRID) $(SINGLE) -o $(OUTDIR)/windows-server-single

client-linux:
	@echo. 
	@echo == Publishing Client (Linux AOT) ==
	dotnet publish $(CLIENT_CLI_PROJ) -c $(CONFIG) -r $(LINUXRID) $(AOT) -o $(OUTDIR)/linux-client

client-linux-single:
	@echo. 
	@echo == Publishing Client (Linux Single File) ==
	dotnet publish $(CLIENT_CLI_PROJ) -c $(CONFIG) -r $(LINUXRID) $(SINGLE) -o $(OUTDIR)/linux-client-single

server-linux:
	@echo. 
	@echo == Publishing Server (Linux single file) ==
	dotnet publish $(SERVER_CLI_PROJ) -c $(CONFIG) -r $(LINUXRID) $(AOT) -o $(OUTDIR)/linux-server

server-linux-single:
	@echo. 
	@echo == Publishing Server (Linux single file) ==
	dotnet publish $(SERVER_CLI_PROJ) -c $(CONFIG) -r $(LINUXRID) $(SINGLE) -o $(OUTDIR)/linux-server-single

# -----------------------------
# Utilities
# -----------------------------
tree:
	@echo.
	@echo == Output tree ==
	@find $(OUTDIR) -maxdepth 2 -type f -printf %p 2>/dev/null || true
	@echo.

clean:
	@echo Cleaning $(OUTDIR)...
	@rm -rf $(OUTDIR)
