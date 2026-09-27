import fs from 'node:fs';

const worker = fs.readFileSync('worker/outlaw-guide.js', 'utf8');

const need = (fragment, message) => {
  if (!worker.includes(fragment)) throw new Error(message);
};

need("const VERSION = 'outlaw-2026.09.27-r3.8'", 'Outlaw worker generation must be r3.8');
need("coordinateSpace: { type: 'string', enum: ['image_px', 'none'] }",
  'planner schema must declare explicit screenshot-pixel coordinates');
need('coordinateImageWidth', 'planner schema must bind geometry to screenshot width');
need('coordinateImageHeight', 'planner schema must bind geometry to screenshot height');
need('Never use a 0-1000 normalized coordinate system',
  'vision prompt must prohibit the ambiguous legacy normalized coordinate contract');
need('independent multimodal grounding reviewer',
  'stage 2 must independently see and re-ground the screenshot');
need('runGuidance(env, VISION_MODEL, reviewPrompt, reviewPayload, image)',
  'stage 2 must receive the same screenshot');
need('runGeminiGuidance(env, reviewPrompt, geminiReviewPayload, image)',
  'Gemini visual targets must also receive independent screenshot review');
need('reconcileVisionDecision(', 'visual targets must pass a geometry reconciliation gate');
need('visionGeometryAgrees(', 'visual targets must compare two independent rectangles');
need("visual_coordinate_image_size_mismatch",
  'server must reject coordinates declared against the wrong image size');
need("visual_geometry_out_of_image_bounds",
  'server must reject visual rectangles outside the screenshot');
need("visualCoordinateSpace: 'image_px'",
  'health endpoint must expose the current coordinate contract');
need('independentVisionReview: true',
  'health endpoint must expose independent visual review');
need('geometryConsensus: true',
  'health endpoint must expose geometry consensus');
need('visualConsensusField: true',
  'health endpoint must expose dual-pass visual consensus attestation');
need('visualConsensus: true',
  'reconciled visual targets must be explicitly attested by the server');
need('contractFailureFailClosed: true',
  'health contract must advertise fail-closed planner contract handling');
need('visualMetadataRepair: true',
  'health contract must advertise bounded visual metadata repair');
need('validationFailureDecision(',
  'planner schema faults must degrade to a safe not_found decision instead of HTTP 502');
need("return json(visualDisagreement(preliminary, reviewChecked.error))",
  'an invalid second visual pass must not return an unattested preliminary rectangle');
need("return json(visualDisagreement(preliminary, 'review_failed'))",
  'a failed second visual pass must fail closed');
need('const geometryFitsImage =',
  'legacy coordinate metadata may only be repaired when the existing rectangle already fits the current image');

console.log('HelpSys Outlaw worker grounding contract passed.');
