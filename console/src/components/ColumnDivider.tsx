import type { PointerEvent as ReactPointerEvent } from "react";

/**
 * A draggable (and arrow-key) vertical splitter between two panes. `width` is the width of the pane it resizes; `direction`
 * is +1 when dragging right grows that pane (the pane is left of the divider) and -1 when dragging right shrinks it.
 */
export default function ColumnDivider({ label, width, min, max, direction, onChange }: {
  label: string; width: number; min: number; max: number; direction: 1 | -1; onChange: (width: number) => void;
}) {
  const clamp = (w: number) => Math.min(max, Math.max(min, Math.round(w)));
  const onPointerDown = (e: ReactPointerEvent) => {
    e.preventDefault();
    const startX = e.clientX;
    const startW = width;
    const move = (ev: PointerEvent) => onChange(clamp(startW + direction * (ev.clientX - startX)));
    const up = () => {
      window.removeEventListener("pointermove", move);
      window.removeEventListener("pointerup", up);
    };
    window.addEventListener("pointermove", move);
    window.addEventListener("pointerup", up);
  };
  return (
    <div
      className="column-divider"
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
