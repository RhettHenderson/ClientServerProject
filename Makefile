
# =============================
# Project paths (new structure)
# =============================
COMMON_PROJ := Libraries/Common/Common.csproj
SERVER_PROJ := Libraries/ServerApp/ServerApp.csproj
CLIENT_PROJ := Libraries/ClientApp/ClientApp.csproj
CLI_PROJ    := ServerInterfaces/CLI/CLI.csproj

# =============================
# Build settings
# =============================
CONFIG   := Release
OUTDIR   := publish

# RIDs
WINRID   := win-x64
LINUXRID := linux-x64

# -----------------------------
# Top-level targets
# -----------------------------
.PHONY: all clean tree libraries \
        build-cli publish-cli-win publish-cli-linux publish-cli-win-single publish-cli-linux-single \
        publish-cli-win-aot publish-cli-linux-aot \
        build-server publish-server-win publish-server-linux publish-server-win-single publish-server-linux-single \
        publish-server-win-aot publish-server-linux-aot

all: libraries build-cli

# -----------------------------
# Libraries (build only; no publish)
# -----------------------------
libraries: common serverapp clientapp

common:
	@echo "== Building Common =="
	dotnet build $(COMMON_PROJ) -c $(CONFIG)

serverapp:
	@echo "== Building ServerApp =="
	dotnet build $(SERVER_PROJ) -c $(CONFIG)

clientapp:
	@echo "== Building ClientApp =="
	dotnet build $(CLIENT_PROJ) -c $(CONFIG)

# -----------------------------
# CLI build / publish
# -----------------------------
build-cli:
	@echo "== Building CLI =="
	dotnet build $(CLI_PROJ) -c $(CONFIG)

cli-win:
	@echo "== Publishing CLI (Windows framework-dependent) =="
	dotnet publish $(CLI_PROJ) -c $(CONFIG) -r $(WINRID) --self-contained false \
	-o $(OUTDIR)/cli-$(WINRID)

cli-linux:
	@echo "== Publishing CLI (Linux framework-dependent) =="
	dotnet publish $(CLI_PROJ) -c $(CONFIG) -r $(LINUXRID) --self-contained false \
	-o $(OUTDIR)/cli-$(LINUXRID)

cli-win-single:
	@echo "== Publishing CLI (Windows single-file) =="
	dotnet publish $(CLI_PROJ) -c $(CONFIG) -r $(WINRID) --self-contained true \
	-p:PublishSingleFile=true -p:DebugType=none \
	-o $(OUTDIR)/cli-$(WINRID)-single

cli-linux-single:
	@echo "== Publishing CLI (Linux single-file) =="
	dotnet publish $(CLI_PROJ) -c $(CONFIG) -r $(LINUXRID) --self-contained true \
	-p:PublishSingleFile=true -p:DebugType=none \
	-o $(OUTDIR)/cli-$(LINUXRID)-single

cli-win-aot:
	@echo "== Publishing CLI (Windows NativeAOT single-file) =="
	dotnet publish $(CLI_PROJ) -c $(CONFIG) -r $(WINRID) --self-contained true \
	-p:PublishAot=true -p:StripSymbols=true -p:DebugType=none \
	-o $(OUTDIR)/cli-$(WINRID)-aot

cli-linux-aot:
	@echo "== Publishing CLI (Linux NativeAOT single-file) =="
	dotnet publish $(CLI_PROJ) -c $(CONFIG) -r $(LINUXRID) --self-contained true \
	-p:PublishAot=true -p:StripSymbols=true -p:DebugType=none \
	-o $(OUTDIR)/cli-$(LINUXRID)-aot

# -----------------------------
# Server publish helpers
# (only useful if ServerApp is an EXE; otherwise skip these)
# -----------------------------
build-server:
	@echo "== Building ServerApp =="
	dotnet build $(SERVER_PROJ) -c $(CONFIG)

server-win-single:
	@echo "== Publishing Server (Windows single-file) =="
	dotnet publish $(SERVER_PROJ) -c $(CONFIG) -r $(WINRID) --self-contained true \
	-p:PublishSingleFile=true -p:DebugType=none \
	-o $(OUTDIR)/server-$(WINRID)-single

server-linux-single:
	@echo "== Publishing Server (Linux single-file) =="
	dotnet publish $(SERVER_PROJ) -c $(CONFIG) -r $(LINUXRID) --self-contained true \
	-p:PublishSingleFile=true -p:DebugType=none \
	-o $(OUTDIR)/server-$(LINUXRID)-single

server-win-aot:
	@echo "== Publishing Server (Windows NativeAOT single-file) =="
	dotnet publish $(SERVER_PROJ) -c $(CONFIG) -r $(WINRID) --self-contained true \
	-p:PublishAot=true -p:StripSymbols=true -p:DebugType=none \
	-o $(OUTDIR)/server-$(WINRID)-aot

# -----------------------------
# Utilities
# -----------------------------
tree:
	@echo
	@echo "== Output tree =="
	@find $(OUTDIR) -maxdepth 2 -type f -printf "%p\n" 2>/dev/null || true
	@echo

clean:
	@echo "Cleaning $(OUTDIR)..."
	@rm -rf $(OUTDIR)
