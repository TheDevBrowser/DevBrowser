namespace DeveloperBrowser.Core.Browser;

/// <summary>Renders top-level JSON documents without fetching the response again.</summary>
public static class JsonDocumentViewer
{
    public const string Script = """"
        (() => {
            if (window !== window.top) return;
            const render = () => {
                const mime = document.contentType.split(';')[0].trim().toLowerCase();
                if (!/^(application|text)\/(json|[^/]+\+json)$/.test(mime)) return;
                const source = document.querySelector('body > pre');
                if (!source) return;
                const raw = source.textContent;
                let value;
                try { value = JSON.parse(raw); } catch { return; }

                const element = (tag, text, className) => {
                    const node = document.createElement(tag);
                    if (text !== undefined) node.textContent = text;
                    if (className) node.className = className;
                    return node;
                };
                const style = element('style');
                style.textContent = `
                    :root { color-scheme: light dark; }
                    body { margin: 0; font: 14px/1.6 system-ui, sans-serif; }
                    header { position: sticky; top: 0; padding: 12px 20px; background: Canvas;
                        border-bottom: 1px solid GrayText; display: flex; gap: 12px; align-items: center; z-index: 1; }
                    button { font: inherit; cursor: pointer; padding: 3px 12px; }
                    .view-switch { display: inline-flex; gap: 3px; padding: 3px; border-radius: 9px;
                        border: 1px solid light-dark(#d8e0ea, #344256); background: light-dark(#eef2f7, #182230); }
                    .view-switch button { appearance: none; border: 1px solid transparent; border-radius: 6px;
                        background: transparent; color: light-dark(#526176, #aab9cc); min-width: 64px;
                        padding: 4px 14px; font-size: 12px; font-weight: 600; line-height: 20px; }
                    .view-switch button:hover { background: light-dark(#e0e8f2, #26364b);
                        color: light-dark(#172b45, #edf4ff); }
                    .view-switch button[aria-pressed="true"] { background: light-dark(#ffffff, #304966);
                        color: light-dark(#1859a9, #d6e9ff); border-color: light-dark(#cbd9e9, #49688b);
                        box-shadow: 0 1px 3px #00000018; }
                    .view-switch button:focus-visible { outline: 2px solid light-dark(#2563eb, #93c5fd);
                        outline-offset: 2px; }
                    @media (forced-colors: active) {
                        .view-switch button[aria-pressed="true"] { border-color: Highlight; color: Highlight; }
                    }
                    main { padding: 16px 24px; font: 13px/1.7 Consolas, monospace; }
                    details > div { margin-left: 22px; border-left: 1px solid GrayText; padding-left: 12px; }
                    summary { cursor: pointer; width: fit-content; }
                    .key { color: light-dark(#854600, #ffc785); }
                    .string { color: light-dark(#166534, #86efac); }
                    .number, .boolean { color: light-dark(#1d4ed8, #93c5fd); }
                    .null, .meta { color: GrayText; }
                    .leaf { white-space: pre-wrap; overflow-wrap: anywhere; }
                    pre { white-space: pre-wrap; overflow-wrap: anywhere; margin: 0; }
                    [hidden] { display: none !important; }
                `;
                const header = element('header');
                header.append(element('strong', 'JSON'));
                const treeButton = element('button', 'Tree');
                const rawButton = element('button', 'Raw');
                const viewSwitch = element('div', undefined, 'view-switch');
                viewSwitch.setAttribute('role', 'group');
                viewSwitch.setAttribute('aria-label', 'JSON view');
                viewSwitch.append(treeButton, rawButton);
                header.append(viewSwitch);
                const main = element('main');
                const tree = element('section');
                tree.setAttribute('aria-label', 'JSON tree');
                const rawView = element('pre', raw);
                rawView.hidden = true;
                const select = showTree => {
                    tree.hidden = !showTree;
                    rawView.hidden = showTree;
                    treeButton.setAttribute('aria-pressed', String(showTree));
                    rawButton.setAttribute('aria-pressed', String(!showTree));
                };
                treeButton.addEventListener('click', () => select(true));
                rawButton.addEventListener('click', () => select(false));
                select(true);

                const appendNode = (parent, key, data, root = false) => {
                    const label = key === null ? '' : key + ': ';
                    if (data === null || typeof data !== 'object') {
                        const row = element('div', undefined, 'leaf');
                        row.append(element('span', label, 'key'),
                            element('span', JSON.stringify(data), data === null ? 'null' : typeof data));
                        parent.append(row);
                        return;
                    }
                    const array = Array.isArray(data);
                    const keys = Object.keys(data);
                    const branch = element('details');
                    const summary = element('summary');
                    summary.append(element('span', label, 'key'), element('span',
                        array ? `Array (${keys.length})` : `Object (${keys.length})`, 'meta'));
                    const children = element('div');
                    branch.append(summary, children);
                    let offset = 0;
                    let loaded = false;
                    const more = element('button', 'Show next 100');
                    const load = () => {
                        more.remove();
                        const end = Math.min(offset + 100, keys.length);
                        for (; offset < end; offset++) {
                            const key = keys[offset];
                            appendNode(children, array ? `[${key}]` : JSON.stringify(key), data[key]);
                        }
                        if (offset < keys.length) children.append(more);
                        loaded = true;
                    };
                    more.addEventListener('click', load);
                    branch.addEventListener('toggle', () => { if (branch.open && !loaded) load(); });
                    parent.append(branch);
                    if (root) { load(); branch.open = true; }
                };
                appendNode(tree, null, value, true);
                main.append(tree, rawView);
                document.head.append(style);
                document.body.replaceChildren(header, main);
            };
            if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', render, { once: true });
            else render();
        })();
        """";
}
