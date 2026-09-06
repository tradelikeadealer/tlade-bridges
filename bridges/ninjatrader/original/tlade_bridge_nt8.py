from flask import Flask, request, jsonify

app = Flask(__name__)

@app.route('/push_spot', methods=['POST'])
def push_spot():
    raw = request.get_data(as_text=True)
    print(f"[RAW] {raw}")
    data = request.json
    if data is None:
        print("[ERROR] JSON non valido!")
        return jsonify({"error": "invalid json"}), 400
    print(f"[TICK] {data['ticker']} → {data['spot']} @ {data['ts']}")
    return jsonify({"ok": True})

if __name__ == '__main__':
    print("[TLADe] Receiver in ascolto su http://localhost:8765")
    app.run(port=8765, debug=False)
