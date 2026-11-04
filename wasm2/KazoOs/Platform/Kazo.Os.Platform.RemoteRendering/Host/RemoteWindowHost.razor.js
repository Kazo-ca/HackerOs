// Page side of a window rendered in a Web Worker (ADR 0129 trial, ADR 0133).
//
// The worker keeps the window's logical tree and resolves every Blazor edit to plain DOM operations
// on numbered nodes (DomPatchWriter.cs); this module only executes them on the window's content
// element and sends DOM events back. It never touches Blazor's own JavaScript (blazor.web.js): the
// format is ours, versioned with the C# that writes it. Node 0 is the content element.
//
// Operations ([code, id, ...]):
//   1 create element [id, parent, before, tag, svg]   2 create text [id, parent, before, text]
//   3 insert markup  [id, parent, before, html]       4 remove [id]
//   5 set attribute  [id, name, value]                6 remove attribute [id, name]
//   7 set text       [id, text]                       8 listen [id, event, handler] (0 = stop)
//   9 move           [id, parent, before]            10 event flag [id, 'p'|'s', event, on]

const SVG = 'http://www.w3.org/2000/svg';

export function attach(surface, dotnet) {
    return new RemoteSurface(surface, dotnet);
}

class RemoteSurface {
    constructor(surface, dotnet) {
        this.surface = surface;
        this.dotnet = dotnet;
        // id -> Node, or Node[] for inserted markup.
        this.nodes = new Map([[0, surface]]);
        // Element -> Map(event -> { handler, listener, prevent, stop }).
        this.events = new Map();
        // Diagnostics read by the trial's measurements (ADR 0133): batches, bytes, encode time.
        this.stats = { batches: 0, bytes: 0, firstBytes: 0, operations: 0, workerMicroseconds: 0, nodes: 0, lastAppliedAt: 0 };
        surface.__hackerosRemoteWindow = this.stats;
    }

    apply(json) {
        const batch = JSON.parse(json);
        for (const op of batch.o) {
            this.run(op);
        }

        if (this.stats.batches === 0) {
            this.stats.firstBytes = json.length;
        }

        this.stats.batches++;
        this.stats.bytes += json.length;
        this.stats.operations += batch.o.length;
        this.stats.workerMicroseconds += batch.us ?? 0;
        this.stats.nodes = batch.nodes ?? this.stats.nodes;
        this.stats.lastAppliedAt = performance.now();
    }

    run(op) {
        switch (op[0]) {
            case 1: {
                const element = op[5] ? document.createElementNS(SVG, op[4]) : document.createElement(op[4]);
                this.insert(op[1], element, op[2], op[3]);
                break;
            }
            case 2:
                this.insert(op[1], document.createTextNode(op[4]), op[2], op[3]);
                break;
            case 3: {
                const template = document.createElement('template');
                template.innerHTML = op[4];
                const nodes = Array.from(template.content.childNodes);
                const parent = this.first(op[2]);
                const before = op[3] ? this.first(op[3]) : null;
                for (const node of nodes) {
                    parent.insertBefore(node, before);
                }
                // An empty markup string still needs a place: an empty text node holds it.
                if (nodes.length === 0) {
                    const placeholder = document.createTextNode('');
                    parent.insertBefore(placeholder, before);
                    nodes.push(placeholder);
                }
                this.nodes.set(op[1], nodes);
                break;
            }
            case 4:
                for (const node of this.all(op[1])) {
                    this.forget(node);
                    node.remove();
                }
                this.nodes.delete(op[1]);
                break;
            case 5:
                setAttribute(this.first(op[1]), op[2], op[3]);
                break;
            case 6:
                removeAttribute(this.first(op[1]), op[2]);
                break;
            case 7:
                this.first(op[1]).textContent = op[2];
                break;
            case 8:
                this.listen(this.first(op[1]), op[2], op[3]);
                break;
            case 9: {
                const parent = this.first(op[2]);
                const before = op[3] ? this.first(op[3]) : null;
                for (const node of this.all(op[1])) {
                    parent.insertBefore(node, before);
                }
                break;
            }
            case 10: {
                const entry = this.entry(this.first(op[1]), op[3]);
                entry[op[2] === 'p' ? 'prevent' : 'stop'] = op[4] === 1;
                break;
            }
        }
    }

    insert(id, node, parentId, beforeId) {
        const parent = this.first(parentId);
        parent.insertBefore(node, beforeId ? this.first(beforeId) : null);
        this.nodes.set(id, node);
    }

    first(id) {
        const node = this.nodes.get(id);
        return Array.isArray(node) ? node[0] : node;
    }

    all(id) {
        const node = this.nodes.get(id);
        return node === undefined ? [] : Array.isArray(node) ? node : [node];
    }

    entry(element, eventName) {
        let events = this.events.get(element);
        if (!events) {
            events = new Map();
            this.events.set(element, events);
        }

        let entry = events.get(eventName);
        if (!entry) {
            entry = { handler: 0, listener: null, prevent: false, stop: false };
            events.set(eventName, entry);
        }

        return entry;
    }

    listen(element, eventName, handler) {
        const entry = this.entry(element, eventName);
        entry.handler = handler;
        if (handler === 0) {
            if (entry.listener) {
                element.removeEventListener(eventName, entry.listener);
                entry.listener = null;
            }
            return;
        }

        if (!entry.listener) {
            entry.listener = event => {
                if (entry.prevent) {
                    event.preventDefault();
                }
                if (entry.stop) {
                    event.stopPropagation();
                }
                if (entry.handler !== 0) {
                    this.stats.lastEventAt = performance.now();
                    this.dotnet.invokeMethod('DispatchEvent', entry.handler, eventName, JSON.stringify(describe(event)));
                }
            };
            element.addEventListener(eventName, entry.listener);
        }
    }

    // Drops the listeners of a removed subtree, so a removed element holds no handler ids.
    forget(node) {
        if (node.nodeType !== Node.ELEMENT_NODE) {
            return;
        }

        for (const element of [node, ...node.querySelectorAll('*')]) {
            const events = this.events.get(element);
            if (events) {
                for (const [eventName, entry] of events) {
                    if (entry.listener) {
                        element.removeEventListener(eventName, entry.listener);
                    }
                }
                this.events.delete(element);
            }
        }
    }

    dispose() {
        for (const id of [...this.nodes.keys()]) {
            if (id !== 0) {
                for (const node of this.all(id)) {
                    this.forget(node);
                }
            }
        }

        this.nodes.clear();
        this.events.clear();
        this.surface.replaceChildren();
        delete this.surface.__hackerosRemoteWindow;
    }
}

// value and checked of form fields are properties: the attribute is only their initial value.
function setAttribute(element, name, value) {
    if (name === 'value' && 'value' in element && element.tagName !== 'BUTTON' && element.tagName !== 'OPTION') {
        if (element.value !== value) {
            element.value = value;
        }
        return;
    }

    if (name === 'checked' && 'checked' in element) {
        element.checked = true;
        return;
    }

    element.setAttribute(name, value);
}

function removeAttribute(element, name) {
    if (name === 'value' && 'value' in element && element.tagName !== 'BUTTON' && element.tagName !== 'OPTION') {
        element.value = '';
        return;
    }

    if (name === 'checked' && 'checked' in element) {
        element.checked = false;
        return;
    }

    element.removeAttribute(name);
}

// The plain properties of a DOM event the worker turns back into Blazor's EventArgs (DomEventArgsReader.cs).
function describe(event) {
    const a = { type: event.type };
    if (event instanceof KeyboardEvent) {
        Object.assign(a, {
            key: event.key, code: event.code, location: event.location, repeat: event.repeat,
            ctrlKey: event.ctrlKey, shiftKey: event.shiftKey, altKey: event.altKey, metaKey: event.metaKey
        });
    } else if (event instanceof MouseEvent) {
        Object.assign(a, {
            detail: event.detail, screenX: event.screenX, screenY: event.screenY, clientX: event.clientX,
            clientY: event.clientY, offsetX: event.offsetX, offsetY: event.offsetY, pageX: event.pageX,
            pageY: event.pageY, movementX: event.movementX, movementY: event.movementY, button: event.button,
            buttons: event.buttons, ctrlKey: event.ctrlKey, shiftKey: event.shiftKey, altKey: event.altKey,
            metaKey: event.metaKey
        });
        if (typeof PointerEvent !== 'undefined' && event instanceof PointerEvent) {
            Object.assign(a, { pointerId: event.pointerId, pointerType: event.pointerType, isPrimary: event.isPrimary });
        }
        if (event instanceof WheelEvent) {
            Object.assign(a, { deltaX: event.deltaX, deltaY: event.deltaY, deltaZ: event.deltaZ, deltaMode: event.deltaMode });
        }
    }

    if (event.type === 'input' || event.type === 'change') {
        const target = event.target;
        a.value = target.type === 'checkbox' || target.type === 'radio' ? target.checked : target.value;
    }

    return a;
}
