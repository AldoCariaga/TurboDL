namespace TurboDL.App.Services;

/// <summary>
/// The script <see cref="BrowserCapture"/> runs on a file host's page about once a second to see what the page is waiting for.
/// It answers <c>"captcha|"</c> (a visible CAPTCHA isn't answered yet: the user solves it, never TurboDL),
/// <c>"timer|&lt;seconds&gt;"</c> (a countdown is still running), <c>"busy|"</c> (the page says it's still making the link),
/// <c>"click|&lt;label&gt;"</c> (a download button is ready; it is
/// marked <c>data-turbodl-target</c> for a real mouse click), or <c>"none|"</c>.
/// Buttons are found by their visible text, not by ids, and anything that leads to another site (ads), asks for money or
/// installs something is skipped. Each button is picked once, so a click that didn't work is left to the user.
/// </summary>
internal static class HostPageScript
{
    public const string TargetSelector = "[data-turbodl-target]";

    /// <summary>
    /// The argument is <c>{ ignoreTimers, ignoreBusy }</c>: a countdown that never changes, or a "please wait" that never
    /// goes away, is page text, and is ignored.
    /// </summary>
    public const string Scan = """
        ({ ignoreTimers, ignoreBusy }) => {
          const captchaUnsolved =
        """ + CaptchaScript.Unsolved + """
        ;
          const visible = el => {
            if (!el || !el.isConnected) return false;
            const s = getComputedStyle(el);
            if (s.display === 'none' || s.visibility === 'hidden' || parseFloat(s.opacity) === 0) return false;
            const r = el.getBoundingClientRect();
            return r.width > 2 && r.height > 2;
          };
          const site = host => host.split('.').slice(-2).join('.');
          const label = el => ((el.innerText || el.value || el.getAttribute('aria-label') || el.title || '') + '')
            .replace(/\s+/g, ' ').trim();

          for (const el of document.querySelectorAll('[data-turbodl-target]')) el.removeAttribute('data-turbodl-target');

          // A CAPTCHA the user still has to solve (CaptchaScript).
          if (captchaUnsolved()) return 'captcha|';

          // A countdown still running: the longest one shown.
          if (!ignoreTimers) {
            const wordRe = /(?:wait|countdown|ready in|available in|starts? in|seconds? left|remaining)\D{0,25}?(\d{1,4})/i;
            const secondsRe = /\b(\d{1,4})\s*(?:s|sec|secs|second|seconds)\b/i;
            const clockRe = /\b(\d{1,2}):([0-5]\d)\b/;
            let wait = 0;
            for (const el of document.querySelectorAll('body *')) {
              if (el.children.length > 3) continue;
              const raw = el.textContent;
              if (!raw || raw.length > 160) continue;
              const t = raw.replace(/\s+/g, ' ').trim();
              if (!t) continue;
              const idc = ((el.id || '') + ' ' + (typeof el.className === 'string' ? el.className : '')).toLowerCase();
              const timerish = /count|timer|wait|clock|second/.test(idc);
              let n = 0, m;
              if (timerish && /^\d{1,4}$/.test(t)) n = +t;
              else if ((m = t.match(wordRe))) n = +m[1];
              else if ((m = t.match(secondsRe)) && /wait|download|link|ready|left|remaining/i.test(t)) n = +m[1];
              else if (timerish && (m = t.match(clockRe))) n = +m[1] * 60 + +m[2];
              if (n > 0 && visible(el)) wait = Math.max(wait, n);
            }
            if (wait > 0) return 'timer|' + wait;
          }

          // The host still making the link, with no number shown ("Generating download link...").
          if (!ignoreBusy) {
            const busyRe = /\b(generating|preparing|creating) (your |the )?(download|link|file)|^(please wait|loading)\W*$/i;
            for (const el of document.querySelectorAll('body *')) {
              if (el.children.length > 2) continue;
              const raw = el.textContent;
              if (!raw || raw.length > 80) continue;
              if (busyRe.test(raw.replace(/\s+/g, ' ').trim()) && visible(el)) return 'busy|';
            }
          }

          // The download button, best wording first; ties go to the biggest one on screen.
          const wanted = [/create download link/i, /get (download )?link/i, /(free|slow|regular|normal) download/i,
            /download (now|file)|start download|continue to download|generate link/i, /^\s*download\s*$/i, /download/i,
            /click here/i];
          const clickHere = wanted.length - 1; // only next to words about the download ("If it doesn't start, click here")
          const unwanted = /premium|fast(er)? download|high speed|vip|upgrade|torrent|install|\bapp\b|extension|vpn|register|sign ?up|log ?in|buy|\bpay\b|report|share/i;
          let best = null, bestRank = wanted.length, bestArea = 0;
          for (const el of document.querySelectorAll('a[href], button, input[type=submit], input[type=button], [role=button], [onclick]')) {
            if (el.hasAttribute('data-turbodl-clicked') || el.disabled || el.getAttribute('aria-disabled') === 'true') continue;
            if (/\bdisabled\b/i.test(typeof el.className === 'string' ? el.className : '')) continue;
            const text = label(el);
            if (!text || text.length > 60 || unwanted.test(text)) continue;
            if (el.tagName === 'A') {
              const href = el.getAttribute('href') || '';
              if (/^https?:/i.test(el.href) && /^https?:|^\/\//i.test(href) && site(new URL(el.href).hostname) !== site(location.hostname)) continue;
            }
            const rank = wanted.findIndex(re => re.test(text));
            if (rank < 0 || !visible(el)) continue;
            if (rank === clickHere && !/download|link|file/i.test(el.parentElement?.textContent || '')) continue;
            const r = el.getBoundingClientRect();
            const area = r.width * r.height;
            if (rank < bestRank || (rank === bestRank && area > bestArea)) { best = el; bestRank = rank; bestArea = area; }
          }
          if (best) {
            best.setAttribute('data-turbodl-target', '1');
            best.setAttribute('data-turbodl-clicked', '1');
            return 'click|' + label(best);
          }
          return 'none|';
        }
        """;
}
