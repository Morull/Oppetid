import traceback
import pandas as pd
import parser

path = '../fixtures/drivdal-feb2025.xlsx'
print('Running parse_settlement...')
try:
    parsed = parser.parse_settlement(path)
    print('SUCCESS rows:', len(parsed.hourly))
except Exception:
    traceback.print_exc()
