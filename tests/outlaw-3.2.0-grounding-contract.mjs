import fs from 'node:fs';

const decision = fs.readFileSync('src/HelpSys.Desktop/Models/QualityGuideDecision.cs', 'utf8');
const frame = fs.readFileSync('src/HelpSys.Desktop/Models/ScreenCaptureFrame.cs', 'utf8');
const quality = fs.readFileSync('src/HelpSys.Desktop/MainWindow.QualityFirst.cs', 'utf8');
const reliability = fs.readFileSync('src/HelpSys.Desktop/MainWindow.ReliabilityV3.cs', 'utf8');
const cloud = fs.readFileSync('src/HelpSys.Desktop/Services/CloudGuideService.cs', 'utf8');
const project = fs.readFileSync('src/HelpSys.Desktop/HelpSys.Desktop.csproj', 'utf8');
const release = fs.readFileSync('.github/workflows/outlaw-release.yml', 'utf8');

const normalize = value => String(value).replace(/\s+/g, ' ').trim();
const need = (source, fragment, message) => {
  if (!normalize(source).includes(normalize(fragment))) throw new Error(message);
};

need(decision, 'string? CoordinateSpace = null', 'planner response must carry an explicit visual coordinate space');
need(decision, 'int CoordinateImageWidth = 0', 'planner response must carry source image width');
need(decision, 'int CoordinateImageHeight = 0', 'planner response must carry source image height');
need(decision, 'bool VisualConsensus = false', 'planner response must carry server dual-pass visual consensus');

need(frame, 'public Rect MapImagePixelBounds(', 'visual targets must map from screenshot pixel coordinates');
need(frame, 'coordinateImageWidth != ImageWidth', 'pixel mapping must reject image-width contract mismatch');
need(frame, 'coordinateImageHeight != ImageHeight', 'pixel mapping must reject image-height contract mismatch');

need(quality, '"outlaw_visual_coordinate_contract_rejected"', 'desktop must log and reject coordinate-contract mismatches');
need(quality, '"outlaw_visual_capture_stale"', 'desktop must reject UI changes during screenshot capture');
need(quality, '"outlaw_stale_vision_target"', 'desktop must reject post-capture stale visual targets');
need(quality, 'TryQueueCurrentStateReplan("画像判断中に画面が変化したため、古い座標を破棄して再取得する"', 'stale visual targets must request one fresh observation');
need(quality, 'IsCompatibleVisualSnap(', 'visual coordinates must be cross-checked against accessible geometry when available');
need(quality, 'SnapToAccessibleCandidateAsync(', 'visual target center must be UIA-hit-tested when possible');
need(quality, 'IsVisualTargetOnCurrentSurface(', 'visual targets must remain on the current foreground surface');
need(quality, '!quality.VisualConsensus', 'desktop must reject visual coordinates without server dual-pass consensus');
if (quality.includes('outlaw_visual_capture_changed_ignored') || quality.includes('outlaw_post_capture_change_ignored'))
  throw new Error('3.2.0 must not ignore screenshot freshness changes');

need(reliability, 'HasExpectedSystemTransitionV3(', 'progress verification must use expected state transitions');
need(reliability, 'HasExpectedSemanticEvidenceV3(', 'progress verification must require semantic evidence');
need(reliability, 'BuildExpectedStateAnchorsV3(', 'progress verification must derive expected destination anchors');
need(reliability, '[ぁ-んァ-ヶ一-龯]{2,24}?', 'Japanese destination names must participate in expected-state verification');
need(reliability, '"outlaw_progress_verified"', 'verified expected progress must be logged explicitly');
need(reliability, 'substantialContentChange && (semanticEvidence || targetDisappeared)', 'generic screen change alone must not count as success');

need(cloud, 'frame.ImageWidth', 'outlaw planner must send screenshot image width');
need(cloud, 'frame.ImageHeight', 'outlaw planner must send screenshot image height');
need(project, '<Version>3.2.3</Version>', 'Outlaw 3.2.3 must retain the 3.2.0 grounding contract');
need(project, '<FileVersion>3.2.3.0</FileVersion>', 'Outlaw 3.2.3 file version must be synchronized');
need(release, "$version = '3.2.3'", 'release workflow must publish 3.2.3 while retaining the 3.2.0 asset');
need(release, 'Version: 3.2.3', 'VERSION.txt must identify 3.2.3');

console.log('HelpSys Outlaw 3.2.3 retains the 3.2.0 visual grounding and transition contract.');
