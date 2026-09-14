const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');
const path = require('node:path');

// Execute the shipped script with a minimal DOM, without a separate copy of its logic.
const source = fs.readFileSync(path.join(__dirname,
    '../src/DeveloperBrowser.Core/Browser/JsonDocumentViewer.cs'), 'utf8');
const script = source.split('""""')[1];
class Element {
    constructor(tag) { this.tag = tag; this.children = []; this.events = {}; this.attributes = {}; }
    append(...nodes) { for (const node of nodes) { node.parent = this; this.children.push(node); } }
    replaceChildren(...nodes) { this.children = []; this.append(...nodes); }
    setAttribute(key, value) { this.attributes[key] = value; }
    addEventListener(name, callback) { this.events[name] = callback; }
    remove() { if (this.parent) this.parent.children = this.parent.children.filter(node => node !== this); }
}
function render(raw, mime = 'application/json', frame = false) {
    const pre = new Element('pre'); pre.textContent = raw;
    const document = {
        contentType: mime, readyState: 'loading', head: new Element('head'), body: new Element('body'),
        createElement: tag => new Element(tag), querySelector: () => pre,
        addEventListener: (_, callback) => { document.ready = callback; }
    };
    document.body.append(pre);
    const window = {}; window.top = frame ? {} : window;
    vm.runInNewContext(script, { document, window });
    document.ready?.();
    return document;
}
test('JSON opens as a tree; nested branches load on demand and raw stays exact', () => {
    const raw = '{ "items": [1, true, null], "message": "<script>alert(1)</script>" }';
    const doc = render(raw);
    const [header, main] = doc.body.children;
    const [tree, rawView] = main.children;
    const root = tree.children[0];
    assert.equal(root.open, true);
    const nested = root.children[1].children[0];
    assert.equal(nested.children[1].children.length, 0);
    nested.open = true; nested.events.toggle();
    assert.equal(nested.children[1].children.length, 3);
    assert.equal(rawView.textContent, raw);
    assert.equal(rawView.hidden, true);
    header.children[1].children[1].events.click();
    assert.equal(tree.hidden, true);
    assert.equal(rawView.hidden, false);
    header.children[1].children[0].events.click();
    assert.equal(tree.hidden, false);
    assert.equal(root.children[1].children[1].children[1].textContent, '"<script>alert(1)</script>"');
});
test('large arrays render in batches', () => {
    const doc = render(JSON.stringify(Array.from({ length: 205 }, (_, i) => i)));
    const children = doc.body.children[1].children[0].children[0].children[1];
    assert.equal(children.children.length, 101);
    children.children.at(-1).events.click();
    assert.equal(children.children.length, 201);
    children.children.at(-1).events.click();
    assert.equal(children.children.length, 205);
});
test('JSON MIME variants, empty collections and scalar roots render', () => {
    for (const mime of ['application/problem+json', 'application/json; charset=utf-8', 'text/json'])
        for (const raw of ['{}', '[]', 'null', 'true', '42', '"hello"'])
            assert.equal(render(raw, mime).body.children[0].tag, 'header');
});
test('HTML, malformed JSON and child frames remain unchanged', () => {
    for (const doc of [render('{}', 'text/html'), render('{bad'), render('{}', 'application/json', true)])
        assert.equal(doc.body.children[0].tag, 'pre');
});
