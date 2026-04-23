import parser
import pandas as pd
import traceback

path = '../fixtures/drivdal-feb2025.xlsx'
issues = []

xl = pd.ExcelFile(path, engine='openpyxl')
sheet_names = xl.sheet_names
summary_sheet = sheet_names[0]
hourly_sheet = [s for s in sheet_names if s is not summary_sheet and s != summary_sheet][0]

print('Reading summary...')
summary_raw = pd.read_excel(path, sheet_name=summary_sheet, engine='openpyxl', header=None)
print('Parsing summary...')
summary = parser._parse_summary(summary_raw, issues)
print('Summary OK, cols:', list(summary.columns))

print('Reading hourly...')
hourly_raw = pd.read_excel(path, sheet_name=hourly_sheet, engine='openpyxl', header=1, skiprows=[2])
print('hourly_raw cols:', list(hourly_raw.columns)[:6])
print('Calling _parse_hourly...')
try:
    hourly = parser._parse_hourly(hourly_raw, issues)
    print('OK')
except Exception:
    traceback.print_exc()
