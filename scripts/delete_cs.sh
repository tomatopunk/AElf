#!/bin/bash
p=`pwd`
echo '--------------------------------------------------------------------'
echo 'find.............'
find $p/Protobuf/Generated  -type f  -name *.g.cs
find $p/Protobuf/Generated  -type f  -name *.g.gs
echo 'delete.........'
find $p/Protobuf/Generated  -type f  -name *.g.cs | xargs rm -f
find $p/Protobuf/Generated  -type f  -name *.g.gs | xargs rm -f
echo 'delete after find.............'
find $p/Protobuf/Generated  -type f  -name *.g.cs
find $p/Protobuf/Generated  -type f  -name *.g.gs
echo '---------------------------------------------------------------------'

