#!/bin/bash
p=`pwd`
echo '--------------------------------------------------------------------'
echo 'find.............'
rm -f  $p/Protobuf/Generated/*.g.cs
rm -f  $p/Protobuf/Generated/*.g.gs
echo $?
echo '---------------------------------------------------------------------'

