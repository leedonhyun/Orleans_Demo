type ComposerProps = {
  message: string;
  typingText: string;
  busySend: boolean;
  setMessageValue: (value: string) => void;
  onSend: () => void;
  onRefresh: () => void;
};

export default function Composer(props: ComposerProps) {
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
        <div className="actions">
          <button onClick={props.onSend} disabled={props.busySend}>Send</button>
          <button onClick={props.onRefresh}>Refresh</button>
        </div>
      </section>
    </>
  );
}
