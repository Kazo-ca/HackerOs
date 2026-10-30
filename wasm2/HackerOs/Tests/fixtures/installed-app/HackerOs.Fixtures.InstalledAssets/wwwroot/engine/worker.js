// ADR 0206 test package: a relative importScripts, as a JavaScript engine run in a worker does.
importScripts('engine.js');
self.onmessage = () => self.postMessage(self.engineAnswer());
