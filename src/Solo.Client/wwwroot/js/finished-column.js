const observers = new WeakMap();

export function observe(zone, content, receiver) {
    disconnect(zone);
    const column = zone.closest('.kanban-column');
    const activeZones = [...column.parentElement.querySelectorAll('.kanban-zone')]
        .filter(candidate => candidate !== zone && !candidate.classList.contains('kanban-zone--finished'));
    let previous;
    let disposed = false;
    const measure = () => {
        // Grid cells stretch to the tallest column. Measuring their boxes here
        // would let Finished increase its own limit on every resize; use only
        // the active cards' natural height, padding, and per-column headings.
        const activeHeight = Math.max(0, ...activeZones.map(active => {
            const style = getComputedStyle(active);
            const padding = parseFloat(style.paddingTop) + parseFloat(style.paddingBottom);
            const naturalHeight = active.querySelector('.kanban-zone__content').getBoundingClientRect().height + padding;
            return Math.max(parseFloat(style.minHeight) || 0, naturalHeight)
                + outerHeight(active.parentElement.querySelector('.kanban-column__header'));
        }));
        const controlsHeight = outerHeight(column.querySelector('.kanban-column__header'))
            + outerHeight(column.querySelector('.kanban-column__toggle'));
        const availableHeight = `${Math.max(0, activeHeight - controlsHeight)}px`;
        if (zone.style.getPropertyValue('--kanban-active-height') !== availableHeight) {
            zone.style.setProperty('--kanban-active-height', availableHeight);
        }

        // The inner content keeps its natural height even while the outer zone
        // is capped or its column is collapsed. Card edits and wrapping matter.
        const overflows = zone.scrollHeight > zone.clientHeight + 1;
        if (previous === overflows) return;
        previous = overflows;
        receiver.invokeMethodAsync('OverflowChanged', overflows).catch(error => {
            if (!disposed) console.error('Finished column measurement failed.', error);
        });
    };
    const observer = new ResizeObserver(measure);
    observer.observe(zone);
    observer.observe(content);
    observer.observe(column.querySelector('.kanban-column__header'));
    for (const active of activeZones) {
        observer.observe(active.querySelector('.kanban-zone__content'));
        observer.observe(active.parentElement.querySelector('.kanban-column__header'));
    }
    // The toggle appears outside the zone. Observe it too so its height is
    // deducted even when the lane's taller active columns don't resize.
    let toggle;
    const controls = new MutationObserver(() => {
        if (toggle) observer.unobserve(toggle);
        toggle = column.querySelector('.kanban-column__toggle');
        if (toggle) observer.observe(toggle);
        measure();
    });
    controls.observe(column, { childList: true });
    observers.set(zone, () => {
        disposed = true;
        observer.disconnect();
        controls.disconnect();
    });
}

function outerHeight(element) {
    if (!element) return 0;
    const style = getComputedStyle(element);
    return element.getBoundingClientRect().height
        + parseFloat(style.marginTop) + parseFloat(style.marginBottom);
}

export function disconnect(zone) {
    observers.get(zone)?.();
    observers.delete(zone);
}
