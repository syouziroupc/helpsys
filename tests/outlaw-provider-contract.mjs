import fs from 'node:fs';

const app = fs.readFileSync('src/HelpSys.Desktop/App.xaml.cs', 'utf8');
const main = fs.readFileSync('src/HelpSys.Desktop/MainWindow.xaml.cs', 'utf8');
const xaml = fs.readFileSync('src/HelpSys.Desktop/MainWindow.xaml', 'utf8');
const cloud = fs.readFileSync('src/HelpSys.Desktop/Services/CloudGuideService.cs', 'utf8');
const quality = fs.readFileSync('worker/quality-guide.js', 'utf8');

if (!app.includes('HELPSYS_OUTLAW_AI_PROVIDER') || !app.includes('"auto"'))
  throw new Error('Outlaw must default an unset provider to Auto');
if (!main.includes('"gemini" => "gemini"') || !main.includes('"glm" => "glm"') || !main.includes('_ => "auto"'))
  throw new Error('Desktop provider normalization must preserve auto/gemini/glm');
if (!xaml.includes('AI: Auto (Gemini優先)') || !xaml.includes('Tag="auto" IsSelected="True"'))
  throw new Error('UI must show Gemini-first Auto as the default');
if (!xaml.includes('Content="AI: Gemini" Tag="gemini"'))
  throw new Error('Strict Gemini must remain selectable');
if (!xaml.includes('Content="AI: GLM" Tag="glm"'))
  throw new Error('Strict GLM must remain selectable');
if (xaml.includes('AI: Gemini (未設定)') || xaml.includes('Tag="gemini" IsEnabled="False"'))
  throw new Error('Gemini selector must not be hard-disabled in the desktop build');
if (!cloud.includes('HELPSYS_OUTLAW_AI_PROVIDER') || !cloud.includes('aiProvider = NormalizeOutlawAiProvider'))
  throw new Error('Desktop cloud requests must carry the selected provider');
if (!quality.includes("const useGemini = provider !== 'glm'"))
  throw new Error('Worker Auto/Gemini path must attempt Gemini');
if (!quality.includes("const allowGlmFallback = provider === 'auto'"))
  throw new Error('Only Auto may fall back from Gemini to GLM');
if (!quality.includes("if (provider === 'gemini' && !env.GEMINI_API_KEY)"))
  throw new Error('Strict Gemini must fail closed when Gemini is not configured');
if (!quality.includes('return env.AI.run(model'))
  throw new Error('GLM Workers AI path must remain intact');

console.log('HelpSys Outlaw Gemini-primary provider contract passed.');
