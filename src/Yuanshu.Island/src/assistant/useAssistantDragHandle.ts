import { useRef, type MouseEvent, type PointerEvent } from "react";

const DRAG_THRESHOLD_PX = 4;

export function useAssistantDragHandle(enabled: boolean, beginDrag: () => Promise<void>, openControls?: () => Promise<void>, excludeRight = 0) {
  const gesture = useRef<{ id: number; x: number; y: number } | null>(null);
  const suppressClick = useRef(false);
  return {
    onPointerDown: (event: PointerEvent<HTMLElement>) => {
      if (!enabled || event.button !== 0 || event.isPrimary === false) return;
      if (excludeRight && event.clientX > event.currentTarget.getBoundingClientRect().right - excludeRight) return;
      if (event.target instanceof Element && event.target.closest("button,a,input,textarea,select") !== event.currentTarget && event.target.closest("button,a,input,textarea,select")) return;
      suppressClick.current = false;
      gesture.current = { id: event.pointerId, x: event.clientX, y: event.clientY };
      event.currentTarget.setPointerCapture?.(event.pointerId);
    },
    onPointerMove: (event: PointerEvent<HTMLElement>) => {
      const start = gesture.current;
      if (!enabled || !start || start.id !== event.pointerId) return;
      if ((event.buttons & 1) === 0) { gesture.current = null; return; }
      if (Math.hypot(event.clientX - start.x, event.clientY - start.y) < DRAG_THRESHOLD_PX) return;
      gesture.current = null; suppressClick.current = true;
      if (event.currentTarget.hasPointerCapture?.(event.pointerId)) event.currentTarget.releasePointerCapture(event.pointerId);
      event.preventDefault();
      void beginDrag();
    },
    onPointerUp: () => { gesture.current = null; },
    onPointerCancel: () => { gesture.current = null; },
    onLostPointerCapture: () => { gesture.current = null; },
    onClick: (event: MouseEvent<HTMLElement>) => {
      if (suppressClick.current && event.detail !== 0) { suppressClick.current = false; event.preventDefault(); event.stopPropagation(); return; }
      suppressClick.current = false;
      if (enabled && openControls) void openControls();
    },
  };
}
