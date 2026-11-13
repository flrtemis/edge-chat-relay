using System;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace EdgeChatRelay
{
    /// <summary>
    /// A small auxiliary window for site popups (OAuth sign-in flows etc.).
    /// Shares the parent panel's WebView2 environment, so cookies/logins match.
    /// </summary>
    public class PopupForm : Form
    {
        private readonly WebView2 _web = new WebView2();

        public PopupForm(CoreWebView2Environment env, string? uri)
        {
            Text = "Sign-in window";
            Width = 560;
            Height = 720;
            StartPosition = FormStartPosition.CenterParent;

            _web.Dock = DockStyle.Fill;
            Controls.Add(_web);

            Load += async (s, e) =>
            {
                await _web.EnsureCoreWebView2Async(env);
                _web.CoreWebView2.NewWindowRequested += (s2, e2) =>
                {
                    e2.Handled = true;
                    var p = new PopupForm(env, e2.Uri);
                    p.Show(this);
                };
                _web.CoreWebView2.DocumentTitleChanged += (s2, e2) => Text = string.IsNullOrEmpty(_web.CoreWebView2.DocumentTitle) ? "Sign-in window" : _web.CoreWebView2.DocumentTitle;
                if (!string.IsNullOrEmpty(uri))
                    _web.CoreWebView2.Navigate(uri);
            };
        }
    }
}
