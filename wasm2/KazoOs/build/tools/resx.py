#!/usr/bin/env python3
"""Writes .resx files from a JSON map. Usage: resx.py <out.resx> <json file with {"Key": "Value"}>
Keys are written sorted. Existing file is replaced."""
import json, sys
from xml.sax.saxutils import escape, quoteattr
HEADER = '''<?xml version="1.0" encoding="utf-8"?>
<root>
  <resheader name="resmimetype">
    <value>text/microsoft-resx</value>
  </resheader>
  <resheader name="version">
    <value>2.0</value>
  </resheader>
  <resheader name="reader">
    <value>System.Resources.ResXResourceReader, System.Windows.Forms, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089</value>
  </resheader>
  <resheader name="writer">
    <value>System.Resources.ResXResourceWriter, System.Windows.Forms, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089</value>
  </resheader>
'''
def write(path, entries):
    with open(path, 'w', encoding='utf-8', newline='\n') as f:
        f.write(HEADER)
        for k in sorted(entries):
            f.write(f'  <data name={quoteattr(k)} xml:space="preserve">\n    <value>{escape(entries[k])}</value>\n  </data>\n')
        f.write('</root>\n')
if __name__ == '__main__':
    write(sys.argv[1], json.load(open(sys.argv[2], encoding='utf-8')))
