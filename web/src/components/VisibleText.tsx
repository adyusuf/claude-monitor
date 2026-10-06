import { Fragment } from "react";
import { visibleSegments } from "../lib/visible";

/** Text with everything that is not plain ASCII made visible and highlighted (see lib/visible). Plain text only, never HTML. */
export function VisibleText({ text }: { text: string }) {
  return (
    <>
      {visibleSegments(text).map((s, i) => (
        <Fragment key={i}>
          {s.flagged ? <mark className="vis-flag">{s.text}</mark> : s.text}
          {s.newline ? "\n" : null}
        </Fragment>
      ))}
    </>
  );
}
