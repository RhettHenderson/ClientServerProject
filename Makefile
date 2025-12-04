
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
build-server-cli:
	@echo.
	@echo == Building Server CLI ==
	dotnet build $(SERVER_CLI_PROJ) -c $(CONFIG)

build-client-cli:
	@echo. 
	@echo == Building Client CLI ==
	dotnet build $(CLIENT_CLI_PROJ) -c $(CONFIG)

#For Windows I only publish the full and AOT versions, no single file
#For Linux, I only publish the full and single file versions, no AOT because it's a pain to compile
# -----------------------------
# Interfaces single file and AOT publish
# -----------------------------
client-cli-win:
	@echo. 
	@echo == Publishing Client CLI (Windows AOT) ==
	dotnet publish $(CLIENT_CLI_PROJ) -c $(CONFIG) -r $(WINRID) $(AOT) -o $(OUTDIR)/windows-client-aot

server-cli-win:
	@echo. 
	@echo == Publishing Server CLI (Windows AOT) ==
	dotnet publish $(SERVER_CLI_PROJ) -c $(CONFIG) -r $(WINRID) $(AOT) -o $(OUTDIR)/windows-server-aot

client-cli-linux:
	@echo. 
	@echo == Publishing Client CLI (Linux single file) ==
	dotnet publish $(CLIENT_CLI_PROJ) -c $(CONFIG) -r $(LINUXRID) $(SINGLE) -o $(OUTDIR)/linux-client

server-cli-linux:
	@echo. 
	@echo == Publishing ServerCLI (Linux single file) ==
	dotnet publish $(SERVER_CLI_PROJ) -c $(CONFIG) -r $(LINUXRID) $(SINGLE) -o $(OUTDIR)/linux-server

# -----------------------------
# Interfaces full version publish
# -----------------------------
client-cli-win-full:
	@echo. 
	@echo == Publishing Client CLI (Windows full version) ==
	dotnet publish $(CLIENT_CLI_PROJ) -c $(CONFIG) -r $(WINRID) $(FULL) -o $(OUTDIR)/windows-client-full

server-cli-win-full:
	@echo. 
	@echo == Publishing Server CLI (Windows full version) ==
	dotnet publish $(SERVER_CLI_PROJ) -c $(CONFIG) -r $(WINRID) $(FULL) -o $(OUTDIR)/windows-server-full

client-cli-linux-full:
	@echo. 
	@echo == Publishing Client CLI (Linux full version) ==
	dotnet publish $(CLIENT_CLI_PROJ) -c $(CONFIG) -r $(LINUXRID) $(FULL) -o $(OUTDIR)/linux-client-full

server-cli-linux-full:
	@echo. 
	@echo == Publishing Server CLI (Linux full version) ==
	dotnet publish $(SERVER_CLI_PROJ) -c $(CONFIG) -r $(LINUXRID) $(FULL) -o $(OUTDIR)/linux-server-full



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
