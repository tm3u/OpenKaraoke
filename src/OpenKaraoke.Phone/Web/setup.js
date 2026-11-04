const flows = {
  iphone: ['download', 'files', 'install', 'trust', 'connect', 'record'],
  android: ['download', 'android-install', 'connect', 'record']
};
const panels = [...document.querySelectorAll('[data-step]')];
const platforms = [...document.querySelectorAll('[data-platform]')];
const previous = document.getElementById('setup-previous');
const next = document.getElementById('setup-next');
const progress = document.getElementById('setup-progress');
let platform = 'iphone', index = 0;

function restore() {
  const saved = location.hash.match(/^#(iphone|android)\/([a-z-]+)$/);
  platform = saved ? saved[1] : 'iphone';
  index = saved ? Math.max(0, flows[platform].indexOf(saved[2])) : 0;
}

function render(focus = true) {
  const steps = flows[platform], step = steps[index];
  for (const panel of panels) panel.hidden = panel.dataset.step !== step;
  for (const button of platforms) button.setAttribute('aria-pressed', String(button.dataset.platform === platform));
  progress.max = steps.length;
  progress.value = index + 1;
  document.getElementById('setup-count').textContent = `步驟 ${index + 1} / ${steps.length}`;
  previous.hidden = index === 0;
  next.textContent = index === steps.length - 1 ? '從頭查看' : '下一步';
  next.classList.toggle('primary', index > 0 && index < steps.length - 1);
  document.getElementById('setup-help').open = false;
  history.replaceState(null, '', `#${platform}/${step}`);
  if (focus) {
    document.querySelector(`[data-step="${step}"] h2`).focus({ preventScroll: true });
    window.scrollTo(0, 0);
  }
}

previous.addEventListener('click', () => { index = Math.max(0, index - 1); render(); });
next.addEventListener('click', () => { index = (index + 1) % flows[platform].length; render(); });
for (const button of platforms) button.addEventListener('click', () => {
  platform = button.dataset.platform;
  index = 0;
  render();
});
window.addEventListener('hashchange', () => { restore(); render(); });
restore();
render(false);
document.getElementById('setup-flow').hidden = false;
document.getElementById('setup-actions').hidden = false;
