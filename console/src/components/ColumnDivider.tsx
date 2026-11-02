import type { PointerEvent as ReactPointerEvent } from "react";

/**
 * A draggable (and arrow-key) vertical splitter between two panes. `width` is the width of the pane it resizes; `direction`
 * is +1 when dragging right grows that pane (the pane is left of the divider) and -1 when dragging right shrinks it.
 */
export default function ColumnDivider({ label, width, min, max, direction, onChange, className }: {
  label: string; className?: string; width: number; min: number; max: number; direction: 1 | -1; onChange: (width: number) => void;
}) {
  const clamp = (w: number) => Math.min(max, Math.max(min, Math.round(w)));
  const onPointerDown = (e: ReactPointerEvent<HTMLDivElement>) => {
    e.preventDefault();
    const el = e.currentTarget;
    const startX = e.clientX;
    const startW = width;
    // Capture keeps the drag on this element, and a cancelled gesture (OS, touch) ends it like a release.
    try { el.setPointerCapture(e.pointerId); } catch { /* not supported: the window listeners still work */ }
    const move = (ev: PointerEvent) => onChange(clamp(startW + direction * (ev.clientX - startX)));
    const end = () => {
      window.removeEventListener("pointermove", move);
      window.removeEventListener("pointerup", end);
      window.removeEventListener("pointercancel", end);
    };
    window.addEventListener("pointermove", move);
    window.addEventListener("pointerup", end);
    window.addEventListener("pointercancel", end);
  };
  return (
    <div
      className={className ? `column-divider ${className}` : "column-divider"}
      role="separator"
      aria-orientation="vertical"
      aria-label={label}
      aria-valuemin={min}
      aria-valuemax={max}
      aria-valuenow={width}
      tabIndex={0}
      onKeyDown={(e) => {
        if (e.key === "ArrowLeft") { e.preventDefault(); onChange(clamp(width - direction * 24)); }
        if (e.key === "ArrowRight") { e.preventDefault(); onChange(clamp(width + direction * 24)); }
      }}
      onPointerDown={onPointerDown}
    />
  );
}
