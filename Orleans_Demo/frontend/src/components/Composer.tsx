import { useRef } from "react";

type ComposerProps = {
  message: string;
  typingText: string;
  busySend: boolean;
  setMessageValue: (value: string) => void;
  onSend: () => void;
  onRefresh: () => void;
  onSendFile: (file: File) => void;
};

export default function Composer(props: ComposerProps) {
  const fileInputRef = useRef<HTMLInputElement>(null);

  return (
    <>
      <section className="typing">{props.typingText}</section>

      <section className="footer">
        <input
          placeholder="메시지를 입력하고 Enter를 누르세요"
          value={props.message}
          onChange={(e) => props.setMessageValue(e.target.value)}
          onKeyDown={(e) => {
            if (e.key === "Enter") {
              props.onSend();
            }
          }}
        />
        <input
          ref={fileInputRef}
          type="file"
          className="file-input"
          title="Attach file"
          aria-label="Attach file"
          accept="image/*,.txt,.pdf,.doc,.docx,.xls,.xlsx,.ppt,.pptx"
          onChange={(e) => {
            const selected = e.target.files?.[0];
            if (selected) {
              props.onSendFile(selected);
            }

            // Allow selecting the same file again.
            e.currentTarget.value = "";
          }}
        />
        <div className="actions">
          <button onClick={props.onSend} disabled={props.busySend}>Send</button>
          <button onClick={() => fileInputRef.current?.click()} disabled={props.busySend}>File</button>
          <button onClick={props.onRefresh}>Refresh</button>
        </div>
      </section>
    </>
  );
}
