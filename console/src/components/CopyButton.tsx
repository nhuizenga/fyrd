import { useEffect, useState } from "react";
import { Button } from "@servicetitan/anvil2";
import CheckIcon from "@servicetitan/anvil2/assets/icons/material/round/check.svg?react";
import CopyIcon from "@servicetitan/anvil2/assets/icons/material/round/content_copy.svg?react";

/** Icon button that copies `text`; shows a check for a moment after copying. Revealed on bubble hover by CSS. */
export default function CopyButton({ text }: { text: string }) {
  const [copied, setCopied] = useState(false);
  useEffect(() => {
    if (!copied) return;
    const t = setTimeout(() => setCopied(false), 1500);
    return () => clearTimeout(t);
  }, [copied]);
  return (
    <Button
      className="copy-btn"
      appearance="ghost"
      size="xsmall"
      icon={copied ? CheckIcon : CopyIcon}
      aria-label={copied ? "Copied" : "Copy message"}
      title={copied ? "Copied" : "Copy to clipboard"}
      onClick={() => {
        navigator.clipboard.writeText(text).then(() => setCopied(true), () => {});
      }}
    />
  );
}
