// ADR 0216 test package: a component's collocated module, outside wwwroot/ and never declared in
// the app's Assets; HackerOsPack puts it in the package.
export function label() {
    return 'collocated ready';
}
