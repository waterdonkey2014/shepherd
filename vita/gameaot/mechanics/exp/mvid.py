#!/usr/bin/env python3
# Prints the MVID of each .NET assembly given (the reader of vita/hello/check_aot_guids.py, imported from there).
import importlib.util, os, sys
here = os.path.dirname(os.path.abspath(__file__))
src = open(os.path.join(here, '..', '..', '..', 'hello', 'check_aot_guids.py')).read()
ns = {}
exec(src.split('\ndef tables')[0], ns)          # only the imports and mvid(); the script part needs argv
for f in sys.argv[1:]:
    print('%s %s' % (ns['mvid'](f), f))
