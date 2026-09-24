import fs from 'node:fs';

const worker = fs.readFileSync('worker/outlaw-guide.js', 'utf8');

const need = (fragment, message) => {
  if (!worker.includes(fragment)) throw new Error(message);
};

need("const VERSION = 'outlaw-2026.09.24-r3.7'", 'Outlaw worker generation must be r3.7');
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

console.log('HelpSys Outlaw worker grounding contract passed.');
