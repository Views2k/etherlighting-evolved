"""Start the existing backend with an isolated data directory for frontend QA."""
import argparse
from pathlib import Path
import sys

parser = argparse.ArgumentParser()
parser.add_argument("source", type=Path)
parser.add_argument("--port", type=int, default=8787)
args = parser.parse_args()
sys.path.insert(0, str(args.source / "src"))
from etherlighting_evolved.config import Config
from etherlighting_evolved.api import create_app
import uvicorn

root = Path(__file__).resolve().parent.parent
config = Config()
config.data_dir = root / "outputs" / "backend-preview"
config.base_dir = root
config.api.static_dir = str(root)
config.api.host = "127.0.0.1"
config.api.port = args.port
app = create_app(config, autoconnect=False)
uvicorn.run(app, host="127.0.0.1", port=args.port, access_log=False, log_level="warning")
