"""Generate an external Root publication recipe; never perform HTTP or mint authority."""
import argparse
import json
from pathlib import Path
import kernel_root_route as route

def main():
    parser=argparse.ArgumentParser()
    for name in ('capability','capability-sha256','observation','observation-sha256','authenticated-issuer-receipt'):parser.add_argument('--'+name,required=True)
    args=parser.parse_args()
    row=route.publisher_recipe(route.regular(Path(args.capability),65536),args.capability_sha256,route.regular(Path(args.observation),65536),args.observation_sha256,route.parse(route.regular(Path(args.authenticated_issuer_receipt),65536)),route.now())
    print(json.dumps(row,indent=2))
    return 0
if __name__=='__main__':raise SystemExit(main())
