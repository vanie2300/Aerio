using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.ComponentModel;
using System.IO.Compression;
using System.Net.Http;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace TabloidBrowser;

public partial class MainWindow
{
    private void ReaderMode_Click(object sender, RoutedEventArgs e) => FireAndForget(ToggleReaderModeAsync(), "ToggleReaderModeAsync");

    private Brush GetReaderActiveBrush() => GetBrush("Accent");

    private async Task ToggleReaderModeAsync()
    {
        var tab = _activeTab;
        if (tab?.WebView?.CoreWebView2 is null) return;
        var webView = tab.WebView.CoreWebView2;

        try
        {
            if (tab.IsReaderMode)
            {
                Interlocked.Exchange(ref _pendingReaderTab, null);
                await webView.ExecuteScriptAsync(ReaderViewCleanupScript);
                tab.IsReaderMode = false;
                UpdateReaderUi(false);
                return;
            }

            var extraction = await BuildReaderExtractionScriptAsync();
            Interlocked.Exchange(ref _pendingReaderTab, tab);
            await webView.ExecuteScriptAsync(extraction);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "ToggleReaderModeAsync failed");
        }
    }

    private async Task<string> BuildReaderExtractionScriptAsync()
    {
        var js = await GetReadabilityScriptAsync();
        return $$"""
        (() => {
        {{js}}
        try {
            var doc = document.cloneNode(true);
            var reader = new Readability(doc);
            var article = reader.parse();
            if (article) {
                chrome.webview.postMessage({
                    type: 'reader-content',
                    title: article.title || '',
                    byline: article.byline || null,
                    siteName: article.siteName || null,
                    publishedTime: article.publishedTime || null,
                    content: article.content || ''
                });
            } else {
                chrome.webview.postMessage({ type: 'reader-content', error: 'Could not parse article' });
            }
        } catch(e) {
            chrome.webview.postMessage({ type: 'reader-content', error: e.message });
        }
        })();
        """;
    }

    private async Task RenderReaderViewAsync(BrowserTab tab, ReaderArticle article)
    {
        var webView = tab.WebView?.CoreWebView2;
        if (webView is null) return;

        var json = JsonSerializer.Serialize(article, _jsonOpts);

        var render = $$"""
        (() => {
        function esc(s){return String(s).replace(/&/g,'&amp;').replace(/</g,'&lt;').replace(/>/g,'&gt;').replace(/"/g,'&quot;')}
        var data = {{json}};
        var id = 'tbl-reader-view';
        if (document.getElementById(id)) return;
        var fs = 20, theme = 'dark';
        var css = document.createElement('style');
        css.id = id + '-css';
        css.textContent =
        ':root{--r-fs:'+fs+'px;--r-lh:1.7;--r-bg:#1a1a1a;--r-text:#e0e0e0;--r-muted:#999;--r-bar:#222;--r-border:#333;--r-link:#74b9ff}'+
        '.r-light{--r-bg:#fafafa;--r-text:#222;--r-muted:#888;--r-bar:#eee;--r-border:#ddd;--r-link:#1a73e8}'+
        '#tbl-reader-view{all:initial;position:fixed;top:0;left:0;right:0;bottom:0;z-index:2147483647;overflow-y:auto;background:var(--r-bg);color:var(--r-text);font:var(--r-fs)/var(--r-lh) Georgia,serif}'+
        '#tbl-reader-bar{position:sticky;top:0;z-index:1;display:flex;align-items:center;gap:8px;padding:8px 16px;background:var(--r-bar);border-bottom:1px solid var(--r-border);font-family:sans-serif}'+
        '#tbl-reader-bar button{background:transparent;color:var(--r-text);border:1px solid var(--r-border);border-radius:4px;padding:4px 10px;cursor:pointer;font-size:12px;font-family:inherit}'+
        '#tbl-reader-bar button:hover{background:rgba(255,255,255,0.1)}'+
        '#tbl-reader-progress{position:fixed;top:0;left:0;height:3px;background:var(--r-link);width:0;z-index:2;transition:width 0.1s}'+
        '#tbl-reader-content{max-width:720px;margin:0 auto;padding:40px 24px}'+
        '#tbl-reader-content h1{font-size:1.8em;margin:0 0 8px;font-weight:700}'+
        '#tbl-reader-content .r-meta{color:var(--r-muted);font-size:0.85em;font-family:sans-serif;margin:0 0 32px}'+
        '#tbl-reader-content .r-meta span{margin-right:12px}'+
        '#tbl-reader-content p{margin:0 0 1.2em}'+
        '#tbl-reader-content img{max-width:100%;height:auto;display:block;margin:1.5em auto;border-radius:4px}'+
        '#tbl-reader-content blockquote{border-left:3px solid var(--r-border);margin:1.2em 0;padding:4px 0 4px 20px;color:var(--r-muted)}'+
        '#tbl-reader-content pre,#tbl-reader-content code{background:rgba(255,255,255,0.06);border-radius:3px;font-size:0.9em}'+
        '#tbl-reader-content pre{padding:16px;overflow-x:auto}'+
        '#tbl-reader-content code{padding:2px 5px}'+
        '#tbl-reader-content pre code{padding:0}'+
        '#tbl-reader-content a{color:var(--r-link)}'+
        '#tbl-reader-content h2,#tbl-reader-content h3,#tbl-reader-content h4{margin:1.4em 0 0.4em}'+
        '#tbl-reader-content ul,#tbl-reader-content ol{padding-left:24px}'+
        '#tbl-reader-content figure{margin:1.5em 0;text-align:center}'+
        '#tbl-reader-content figcaption{font-size:0.85em;color:var(--r-muted);margin-top:4px}';
        document.documentElement.appendChild(css);
        var bar = document.createElement('div');
        bar.id = 'tbl-reader-bar';
        bar.innerHTML =
        '<button onclick="chrome.webview.postMessage({type:\'reader-action\',action:\'exit\'})">\u2716 Exit</button>'+
        '<span style="flex:1;font-size:13px;font-weight:600;overflow:hidden;text-overflow:ellipsis;white-space:nowrap">'+esc(data.title)+'</span>'+
        '<button onclick="document.getElementById(\'tbl-reader-view\').style.setProperty(\'--r-fs\',Math.max(12,parseInt(getComputedStyle(document.getElementById(\'tbl-reader-view\')).getPropertyValue(\'--r-fs\'))-2)+\'px\')">A\u2212</button>'+
        '<button onclick="document.getElementById(\'tbl-reader-view\').style.setProperty(\'--r-fs\',Math.min(36,parseInt(getComputedStyle(document.getElementById(\'tbl-reader-view\')).getPropertyValue(\'--r-fs\'))+2)+\'px\')">A+</button>'+
        '<button onclick="var v=document.getElementById(\'tbl-reader-view\');v.classList.toggle(\'r-light\')">\u263C</button>';
        var progress = document.createElement('div');
        progress.id = 'tbl-reader-progress';
        var main = document.createElement('div');
        main.id = 'tbl-reader-content';
        var meta = '';
        if (data.byline) meta += '<span>\u270E '+esc(data.byline)+'</span>';
        if (data.siteName) meta += '<span>'+esc(data.siteName)+'</span>';
        if (data.publishedTime) meta += '<span>'+esc(data.publishedTime.split('T')[0])+'</span>';
        function sanitize(html){
          var parser = new DOMParser();
          var doc = parser.parseFromString(html, 'text/html');
          var w={'P':[],'B':[],'I':[],'STRONG':[],'EM':[],'U':[],'S':[],'BR':[],'HR':[],'A':['href','title'],'IMG':['src','alt','title'],'UL':[],'OL':[],'LI':[],'H1':[],'H2':[],'H3':[],'H4':[],'H5':[],'H6':[],'BLOCKQUOTE':[],'PRE':[],'CODE':[],'SPAN':['title'],'DIV':[]};
          var p=/^https?|mailto|tel|ftp:/i;
          var all=doc.body.querySelectorAll('*');
          for(var i=all.length-1;i>=0;i--){
            var e=all[i];
            var t=e.tagName.toUpperCase();
            if(!w[t]){
              while(e.firstChild)e.parentNode.insertBefore(e.firstChild,e);
              e.remove();
              continue;
            }
            for(var j=e.attributes.length-1;j>=0;j--){
              var a=e.attributes[j].name.toLowerCase();
              if(!w[t].includes(a)||(['href','src'].includes(a)&&!p.test(e.getAttribute(a)))){
                e.removeAttribute(e.attributes[j].name);
              }
            }
            if(t==='A')e.setAttribute('rel','noopener noreferrer nofollow');
          }
          return doc.body.innerHTML;
        }
        main.innerHTML =
        '<h1>'+esc(data.title)+'</h1>'+
        (meta ? '<div class="r-meta">'+meta+'</div>' : '')+
        sanitize(data.content);
        var view = document.createElement('div');
        view.id = id;
        view.appendChild(bar);
        view.appendChild(progress);
        view.appendChild(main);
        document.body.appendChild(view);
        view.addEventListener('scroll',function(){var p=view.scrollTop/(view.scrollHeight-view.clientHeight);progress.style.width=Math.min(100,p*100)+'%'});
        })();
        """;
        await webView.ExecuteScriptAsync(render);
    }

    private async Task TryInjectReaderDetectionAsync(CoreWebView2 webView)
    {
        try
        {
            await webView.ExecuteScriptAsync(ReaderDetectionScript);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Reader detection failed");
        }
    }

    private void SetReaderButtonVisibility(bool visible)
    {
        if (Dispatcher.CheckAccess())
        {
            ApplyReaderButtonVisibility(visible);
        }
        else
        {
            Dispatcher.InvokeAsync(() => ApplyReaderButtonVisibility(visible));
        }
    }

    private void ApplyReaderButtonVisibility(bool visible)
    {
        if (NavToolbar.ReaderButton is not null)
            NavToolbar.ReaderButton.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        if (NavToolbar.ReaderModeMenuItem is not null)
            NavToolbar.ReaderModeMenuItem.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateReaderUi(bool isActive)
    {
        if (NavToolbar.ReaderModeMenuItem is not null)
        {
            NavToolbar.ReaderModeMenuItem.IsChecked = isActive;
        }
        if (NavToolbar.ReaderButton is not null && NavToolbar.ReaderButton.Visibility == Visibility.Visible)
        {
            NavToolbar.ReaderButton.BorderBrush = isActive ? GetReaderActiveBrush() : null;
        }
    }

    private async Task<string> GetReadabilityScriptAsync()
    {
        if (_readabilityCache is not null) return _readabilityCache;
        await _readabilityCacheLock.WaitAsync();
        try
        {
            if (_readabilityCache is not null) return _readabilityCache;

            // The script is embedded in the assembly: a file on disk could be
            // swapped by any same-user process and would then execute inside
            // every reader-mode page.
            const string resourceName = "TabloidBrowser.readability.min.js";
            var assembly = typeof(MainWindow).Assembly;
            await using var stream = assembly.GetManifestResourceStream(resourceName)
                ?? throw new FileNotFoundException(
                    $"Embedded resource '{resourceName}' is missing from {assembly.GetName().Name}.");
            using var reader = new StreamReader(stream);
            _readabilityCache = await reader.ReadToEndAsync().ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(_readabilityCache))
                throw new InvalidOperationException($"Embedded resource '{resourceName}' is empty");
            return _readabilityCache;
        }
        finally { _readabilityCacheLock.Release(); }
    }
}
