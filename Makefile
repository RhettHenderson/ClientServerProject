CLI_PROJ := ClientApp/ClientApp.csproj
GUI_PROJ := GUI/GUI.csproj
SERVER_PROJ := ServerApp/ServerApp.csproj

CONFIG   := Release
OUTDIR   := publish

# RIDs
WINRID   := win-x64
LINUXRID := linux-x64

# -----------------------------
# Top-level targets
# -----------------------------
.PHONY: all clean \
        cli-win-aot cli-linux-aot \
        gui-win-single \
        tree

all: cli-win-aot gui-win-single cli-linux-single server-win server-linux tree

# -----------------------------
# CLI (Windows) - NativeAOT
# -----------------------------
cli-win-aot:
	dotnet publish $(CLI_PROJ) -c $(CONFIG) -r $(WINRID) \
		-p:PublishAot=true \
		-p:InvariantGlobalization=true \
		-p:DebugType=none \
		-p:Optimize=true \
		-o $(OUTDIR)/cli-$(WINRID)-aot

# -----------------------------
# CLI (Linux) - without NativeAOT
# -----------------------------
cli-linux-single:
#AOT disabled because I can't compile AOT for Linux on Windows
	dotnet publish $(CLI_PROJ) -c $(CONFIG) -r $(LINUXRID) \
		-p:SelfContained=true \
		-p:PublishSingleFile=true \
		-p:EnableCompressionInSingleFile=true \
		-p:PublishTrimmed=false \
		-p:TrimMode=partial \
		-p:PublishReadyToRun=false \
		-p:InvariantGlobalization=true \
		-p:DebugType=none \
		-o $(OUTDIR)/cli-$(LINUXRID)-aot

# -----------------------------
# GUI (Windows Forms) - Self-contained Single File
# -----------------------------
gui-win-single:
	dotnet publish $(GUI_PROJ) -c $(CONFIG) -r $(WINRID) \
		-p:SelfContained=true \
		-p:PublishSingleFile=true \
		-p:EnableCompressionInSingleFile=true \
		-p:PublishTrimmed=false \
		-p:TrimMode=partial \
		-p:PublishReadyToRun=false \
		-p:InvariantGlobalization=true \
		-p:DebugType=none \
		-o $(OUTDIR)/gui-$(WINRID)-single

# -----------------------------
# Server CLI (Windows) - NativeAOT
# -----------------------------
server-win:
	dotnet publish $(SERVER_PROJ) -c $(CONFIG) -r $(WINRID) \
	-p:PublishAot=true \
	-p:InvariantGlobalization=true \
	-p:DebugType=none \
	-p:Optimize=true \
	-o $(OUTDIR)/server-$(WINRID)-aot
	
# -----------------------------
# Server CLI (Linux)
# -----------------------------
server-linux:
	dotnet publish $(SERVER_PROJ) -c $(CONFIG) -r $(LINUXRID) \
	-p:SelfContained=true \
	-p:PublishSingleFile=true \
	-p:EnableCompressionInSingleFile=true \
	-p:PublishTrimmed=false \
	-p:TrimMode=partial \
	-p:PublishReadyToRun=false \
	-p:InvariantGlobalization=true \
	-p:DebugType=none \
	-o $(OUTDIR)/server-$(LINUXRID)-single

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