#!/bin/bash
p=`pwd`
echo '--------------------------------------------------------------------'
echo 'find.............'
rm -rf  $p/Protobuf/Generated/*.g.cs
rm -rf  $p/Protobuf/Generated/*.g.gs
echo $?
echo '---------------------------------------------------------------------'

