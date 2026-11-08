namespace TurboDL.App.Services;

/// <summary>
/// The page script that tells whether a CAPTCHA is waiting for the user: a function answering true when a visible
/// reCAPTCHA, hCaptcha or Turnstile has no answer yet. Invisible ones (the reCAPTCHA v3 badge) don't count. TurboDL
/// never solves one: it only brings the browser on screen. <see cref="HostPageScript"/> and
/// <see cref="SiteStepRunner"/> both use it.
/// </summary>
internal static class CaptchaScript
{
    public const string Unsolved = """
        () => {
          const shown = el => {
            if (!el || !el.isConnected) return false;
            const s = getComputedStyle(el);
            if (s.display === 'none' || s.visibility === 'hidden' || parseFloat(s.opacity) === 0) return false;
            const r = el.getBoundingClientRect();
            return r.width > 2 && r.height > 2;
          };
          const challenges = [...document.querySelectorAll(
            '.g-recaptcha, .h-captcha, .cf-turnstile, iframe[src*="recaptcha"], iframe[src*="hcaptcha"], iframe[src*="challenges.cloudflare.com"]')]
            .filter(el => shown(el) && !el.closest('.grecaptcha-badge') && !/size=invisible/.test(el.getAttribute('src') || ''));
          if (challenges.length === 0) return false;
          const answers = [...document.querySelectorAll(
            'textarea[name="g-recaptcha-response"], textarea[name="h-captcha-response"], input[name="cf-turnstile-response"]')];
          return !answers.some(a => a.value);
        }
        """;
}
