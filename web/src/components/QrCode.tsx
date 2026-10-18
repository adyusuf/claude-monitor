import qrcode from "qrcode-generator";

/**
 * A QR code drawn as inline SVG from the text, entirely in the browser: the text (a two-step setup key) never goes to any other host.
 * Always dark squares on a white card, whatever the theme, because a scanner needs the contrast.
 */
export function QrCode({ text, label }: { text: string; label: string }) {
  const qr = qrcode(0, "M");
  qr.addData(text);
  qr.make();
  const size = qr.getModuleCount();
  const margin = 4;
  let path = "";
  for (let row = 0; row < size; row++) {
    for (let col = 0; col < size; col++) {
      if (qr.isDark(row, col)) path += `M${col + margin} ${row + margin}h1v1h-1z`;
    }
  }
  const side = size + margin * 2;
  return (
    <svg className="qr" role="img" aria-label={label} viewBox={`0 0 ${side} ${side}`} shapeRendering="crispEdges">
      <rect width={side} height={side} fill="#ffffff" />
      <path d={path} fill="#000000" />
    </svg>
  );
}
