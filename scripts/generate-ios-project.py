"""Generate a dependency-free Xcode project. Run on any host; build/sign on macOS."""
from pathlib import Path
root = Path(__file__).resolve().parents[1] / 'mobile' / 'ios'
project = root / 'FinishCamera.xcodeproj'
project.mkdir(exist_ok=True)
files = sorted((root / 'FinishCamera').glob('*.swift'))
def ident(n): return f'{n:024X}'
entries = []
for i, file in enumerate(files):
    entries += [f'{ident(100+i)} = {{isa = PBXFileReference; lastKnownFileType = sourcecode.swift; path = {file.name}; sourceTree = "<group>"; }};',
                f'{ident(200+i)} = {{isa = PBXBuildFile; fileRef = {ident(100+i)}; }};']
entries += [
f'{ident(1)} = {{isa = PBXProject; attributes = {{LastUpgradeCheck = 1600; }}; buildConfigurationList = {ident(10)}; compatibilityVersion = "Xcode 14.0"; developmentRegion = en; hasScannedForEncodings = 0; knownRegions = (en, Base); mainGroup = {ident(2)}; productRefGroup = {ident(4)}; projectDirPath = ""; projectRoot = ""; targets = ({ident(5)}); }};',
f'{ident(2)} = {{isa = PBXGroup; children = ({ident(3)}, {ident(4)}); sourceTree = "<group>"; }};',
f'{ident(3)} = {{isa = PBXGroup; children = ({", ".join(ident(100+i) for i in range(len(files)))}, {ident(150)}); path = FinishCamera; sourceTree = "<group>"; }};',
f'{ident(4)} = {{isa = PBXGroup; children = ({ident(6)}); name = Products; sourceTree = "<group>"; }};',
f'{ident(5)} = {{isa = PBXNativeTarget; buildConfigurationList = {ident(11)}; buildPhases = ({ident(7)}, {ident(8)}, {ident(9)}); buildRules = (); dependencies = (); name = FinishCamera; productName = FinishCamera; productReference = {ident(6)}; productType = "com.apple.product-type.application"; }};',
f'{ident(6)} = {{isa = PBXFileReference; explicitFileType = wrapper.application; includeInIndex = 0; path = FinishCamera.app; sourceTree = BUILT_PRODUCTS_DIR; }};',
f'{ident(7)} = {{isa = PBXSourcesBuildPhase; buildActionMask = 2147483647; files = ({", ".join(ident(200+i) for i in range(len(files)))}); runOnlyForDeploymentPostprocessing = 0; }};',
f'{ident(8)} = {{isa = PBXFrameworksBuildPhase; buildActionMask = 2147483647; files = (); runOnlyForDeploymentPostprocessing = 0; }};',
f'{ident(9)} = {{isa = PBXResourcesBuildPhase; buildActionMask = 2147483647; files = (); runOnlyForDeploymentPostprocessing = 0; }};',
f'{ident(150)} = {{isa = PBXFileReference; lastKnownFileType = text.plist.xml; path = Info.plist; sourceTree = "<group>"; }};',
]
for base, ids in [(10,(20,21)), (11,(22,23))]:
    entries.append(f'{ident(base)} = {{isa = XCConfigurationList; buildConfigurations = ({ident(ids[0])}, {ident(ids[1])}); defaultConfigurationIsVisible = 0; defaultConfigurationName = Release; }};')
for i in range(20,24):
    debug = i % 2 == 0
    settings = 'CLANG_ENABLE_MODULES = YES; SDKROOT = iphoneos; IPHONEOS_DEPLOYMENT_TARGET = 16.0; SWIFT_VERSION = 5.0; '
    if i >= 22:
        settings += 'CODE_SIGN_STYLE = Automatic; INFOPLIST_FILE = FinishCamera/Info.plist; PRODUCT_BUNDLE_IDENTIFIER = cn.obstacletimer.finish; PRODUCT_NAME = "$(TARGET_NAME)"; TARGETED_DEVICE_FAMILY = "1,2"; '
    settings += 'SWIFT_OPTIMIZATION_LEVEL = "-Onone"; DEBUG_INFORMATION_FORMAT = dwarf;' if debug else 'SWIFT_OPTIMIZATION_LEVEL = "-O"; DEBUG_INFORMATION_FORMAT = "dwarf-with-dsym";'
    entries.append(f'{ident(i)} = {{isa = XCBuildConfiguration; buildSettings = {{{settings}}}; name = {"Debug" if debug else "Release"}; }};')
(project / 'project.pbxproj').write_text('// !$*UTF8*$!\n{ archiveVersion = 1; classes = {}; objectVersion = 56; objects = {\n' + '\n'.join(entries) + f'\n}}; rootObject = {ident(1)}; }}\n', encoding='utf-8')
shared = project / 'xcshareddata' / 'xcschemes'
shared.mkdir(parents=True, exist_ok=True)
(shared / 'FinishCamera.xcscheme').write_text(f'''<?xml version="1.0" encoding="UTF-8"?>
<Scheme LastUpgradeVersion="1600" version="1.3"><BuildAction parallelizeBuildables="YES" buildImplicitDependencies="YES"><BuildActionEntries><BuildActionEntry buildForTesting="YES" buildForRunning="YES" buildForProfiling="YES" buildForArchiving="YES" buildForAnalyzing="YES"><BuildableReference BuildableIdentifier="primary" BlueprintIdentifier="{ident(5)}" BuildableName="FinishCamera.app" BlueprintName="FinishCamera" ReferencedContainer="container:FinishCamera.xcodeproj"/></BuildActionEntry></BuildActionEntries></BuildAction><LaunchAction buildConfiguration="Debug" selectedDebuggerIdentifier="Xcode.DebuggerFoundation.Debugger.LLDB" selectedLauncherIdentifier="Xcode.IDEFoundation.Launcher.LLDB" launchStyle="0" useCustomWorkingDirectory="NO" ignoresPersistentStateOnLaunch="NO" debugDocumentVersioning="YES" debugServiceExtension="internal" allowLocationSimulation="YES"><BuildableProductRunnable runnableDebuggingMode="0"><BuildableReference BuildableIdentifier="primary" BlueprintIdentifier="{ident(5)}" BuildableName="FinishCamera.app" BlueprintName="FinishCamera" ReferencedContainer="container:FinishCamera.xcodeproj"/></BuildableProductRunnable></LaunchAction><ArchiveAction buildConfiguration="Release" revealArchiveInOrganizer="YES"/></Scheme>''', encoding='utf-8')
print(f'Generated {project} with {len(files)} Swift sources')
