// Column-resize jitter fix for FluentDataGrid.
//
// FluentUI lays the grid out with `fr` units, but the moment you start dragging a
// column separator it rewrites the whole template from `fr` to integer-pixel
// clientWidth values. The rounded pixel sum rarely equals the container width, so
// the grid reflows once on the first drag (the "jump"), and the fixed first column
// (the checkbox) can balloon because its measured width is off.
//
// The fix: keep every resizable grid pinned to its already-resolved PIXEL widths
// from the moment it renders, so a drag has nothing to convert. getComputedStyle
// always returns the resolved pixels for the fr template, so assigning that back is
// visually a no-op. We never touch a grid whose template is already pure pixels
// (so we never fight a real user resize).
//
// Pixels are only right for the width they were measured at, and that width changes:
// the rows arrive after the header and bring a scrollbar that takes 14px, the window
// is resized, the menu on the left is folded away. So the template a grid was given is
// remembered, and whenever the grid's width changes it goes back to that template for
// a moment and is pinned again at the new width. Without that a table kept the widths
// of a wider moment and scrolled sideways by the width of a scrollbar.
//
// A template may also size a column by what is in it ("max-content", "auto"), so that
// a date or a count is never cut off whatever the language of the computer writes it
// like. Such a grid is measured again whenever its rows change as well.

(function () {
    function hasFlex(t) {
        return !t || t === 'none' || t.indexOf('fr') !== -1 || t.indexOf('minmax') !== -1
            || t.indexOf('auto') !== -1 || t.indexOf('content') !== -1;
    }

    const given = new WeakMap();    // grid -> the flexible template it was rendered with
    const pinnedAt = new WeakMap(); // grid -> its width when it was pinned
    const watched = new WeakSet();
    const byContent = new Set();    // grids on the page with a column sized by its content
    const widthOf = grid => Math.round(grid.getBoundingClientRect().width);

    function pin(grid) {
        if (!grid.classList || !grid.classList.contains('grid')) return;
        // Only act while the inline template is still flexible (fr/minmax) or unset.
        // Once it's pixels we leave it alone - that's either our pin or a user resize.
        const inline = grid.style.gridTemplateColumns;
        if (inline && !hasFlex(inline)) return;
        given.set(grid, inline);
        if (/auto|content/.test(inline)) byContent.add(grid); else byContent.delete(grid);
        if (!watched.has(grid)) { watched.add(grid); sizes.observe(grid); }
        const resolved = getComputedStyle(grid).gridTemplateColumns;
        if (hasFlex(resolved)) return;            // not laid out yet; the size watcher comes back to it
        pinnedAt.set(grid, widthOf(grid));
        if (resolved === inline) return;
        grid.style.gridTemplateColumns = resolved; // pin to pixels (no visual change)
    }

    // The grid has another width than the one its pixels were measured at, or other rows: measure again.
    function refit(grid, rowsChanged) {
        if (!grid.isConnected || grid.dataset.cpUserResized === '1' || !given.has(grid)) return;
        const width = widthOf(grid);
        if (width === 0 || (!rowsChanged && width === pinnedAt.get(grid))) return;
        grid.style.gridTemplateColumns = given.get(grid);
        pin(grid);
    }

    const sizes = new ResizeObserver(entries => { for (const entry of entries) refit(entry.target, false); });

    function scan(root) {
        const scope = root instanceof Element ? root : document;
        if (scope.matches && scope.matches('.fluent-data-grid.grid')) pin(scope);
        scope.querySelectorAll && scope.querySelectorAll('.fluent-data-grid.grid').forEach(pin);
    }

    function start() {
        scan(document);
        const mo = new MutationObserver(muts => {
            let changed = null;
            for (const m of muts) {
                if (m.type === 'attributes') {
                    if (m.target.classList && m.target.classList.contains('grid')) pin(m.target);
                    continue;
                }
                if (m.type === 'childList') for (const n of m.addedNodes) if (n.nodeType === 1) scan(n);
                if (byContent.size === 0) continue;
                // Rows came, went or changed their text inside a grid that sizes columns by content.
                const at = m.target.nodeType === 1 ? m.target : m.target.parentElement;
                const grid = at && at.closest && at.closest('.fluent-data-grid.grid');
                if (grid && byContent.has(grid)) (changed || (changed = new Set())).add(grid);
            }
            if (changed) changed.forEach(grid => refit(grid, true));
            byContent.forEach(grid => { if (!grid.isConnected) byContent.delete(grid); });
        });
        mo.observe(document.body, {
            childList: true, subtree: true, characterData: true,
            attributes: true, attributeFilter: ['style'],
        });
        // Remember which grids the user has hand-resized so that a change of width
        // doesn't throw their chosen widths away.
        document.addEventListener('pointerdown', e => {
            const h = e.target.closest && e.target.closest('.actual-resize-handle, .resize-handle');
            const grid = h && h.closest('.fluent-data-grid.grid');
            if (grid) grid.dataset.cpUserResized = '1';
        }, true);
    }

    if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', start);
    else start();
})();
