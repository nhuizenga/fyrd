import { useEffect, useRef, type ReactNode } from "react";

/** Brings a confirm that appears below the fold (in a long details pane) into view as soon as it renders. */
export default function ScrollIntoView({ children }: { children: ReactNode }) {
  const ref = useRef<HTMLDivElement>(null);
  useEffect(() => { ref.current?.scrollIntoView?.({ block: "nearest" }); }, []);
  return <div ref={ref}>{children}</div>;
}
